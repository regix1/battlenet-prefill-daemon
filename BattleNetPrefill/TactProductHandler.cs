#nullable enable

namespace BattleNetPrefill
{
    public sealed class TactProductHandler
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly bool _forcePrefill;
        private readonly IPrefillProgress _progress;
        private readonly PrefillSettings _settings;

        private readonly PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public TactProductHandler(IAnsiConsole ansiConsole, bool forcePrefill = false, IPrefillProgress? progress = null)
            : this(ansiConsole, forcePrefill, progress ?? NullProgress.Instance, AppConfig.Capture())
        {
        }

        internal TactProductHandler(IAnsiConsole ansiConsole, bool forcePrefill, IPrefillProgress progress, PrefillSettings settings)
        {
            _ansiConsole = ansiConsole;
            _forcePrefill = forcePrefill;
            _progress = progress ?? NullProgress.Instance;
            _settings = settings;
        }

        /// <summary>
        /// The accumulated prefill summary for this handler instance, including the real
        /// <see cref="PrefillSummaryResult.TotalBytesTransferred"/> summed across every processed product.
        /// </summary>
        public PrefillSummaryResult Summary => _prefillSummaryResult;

        // A separate handler owns the size estimate; process defaults remain unchanged.
        public async Task<long> GetProductDownloadSizeAsync(
            TactProduct product,
            CancellationToken cancellationToken = default)
        {
            var handler = new TactProductHandler(_ansiConsole, _forcePrefill, NullProgress.Instance,
                _settings with { SkipDownloads = true });
            await handler.ProcessProductAsync(product, cancellationToken);
            return (long)handler.Summary.TotalBytesTransferred.Bytes;
        }

        public async Task ProcessMultipleProductsAsync(
            List<TactProduct> productsToProcess,
            CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();

            var distinctProducts = productsToProcess.Distinct().ToList();
            _ansiConsole.LogMarkupLine($"Prefilling {LightYellow(productsToProcess.Count)} products \n");
            foreach (var productCode in distinctProducts)
            {
                try
                {
                    await ProcessProductAsync(productCode, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    _ansiConsole.LogMarkupLine(Red($"Unexpected download error : {e.Message}  Skipping app..."));
                    _ansiConsole.MarkupLine("");
                    _prefillSummaryResult.FailedApps++;
                }
            }

            _ansiConsole.LogMarkupLine($"Prefill complete! Prefilled {Magenta(distinctProducts.Count)} apps", timer);
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);
        }

        /// <summary>
        /// Downloads a specified game, in the same manner that Battle.net does.  Should be used to pre-fill a LanCache with game data from Blizzard's CDN.
        /// </summary>
        public async Task<ComparisonResult?> ProcessProductAsync(TactProduct product, CancellationToken cancellationToken = default)
        {
            var metadataTimer = Stopwatch.StartNew();

            // Initializing classes, now that we have our CDN info loaded.
            // The size-only pass (AppConfig.SkipDownloads) uses NullProgress so no download progress is emitted;
            // a real prefill passes the live socket sink + app identity so the UI gets preparing/byte progress.
            var progressSink = _settings.SkipDownloads ? NullProgress.Instance : _progress;
            using var cdnRequestManager = new CdnRequestManager(_ansiConsole, _settings, progressSink, product.ProductCode, product.DisplayName);
            var downloadFileHandler = new DownloadFileHandler(cdnRequestManager);
            var configFileHandler = new ConfigFileHandler(cdnRequestManager);
            var installFileHandler = new InstallFileHandler(cdnRequestManager);
            var archiveIndexHandler = new ArchiveIndexHandler(_ansiConsole, cdnRequestManager, product);
            var patchLoader = new PatchLoader(cdnRequestManager);
            await cdnRequestManager.InitializeAsync(product, cancellationToken);

            // Finding the latest version of the game
            VersionsEntry? targetVersion = await configFileHandler.GetLatestVersionEntryAsync(product, cancellationToken);

            // Skip prefilling if we've already prefilled the latest version
            if (!_forcePrefill && IsProductUpToDate(product, targetVersion.Value))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                return null;
            }

            _ansiConsole.LogMarkupLine($"Starting {Cyan(product.DisplayName)}");

            await _ansiConsole.StatusSpinner().StartAsync("Start", async ctx =>
            {
                // Getting other configuration files for this version, that detail where we can download the required files from.
                ctx.Status("Getting latest config files...");
                BuildConfigFile buildConfig = await BuildConfigParser.GetBuildConfigAsync(
                    targetVersion.Value,
                    cdnRequestManager,
                    cancellationToken);
                CDNConfigFile cdnConfig = await configFileHandler.GetCdnConfigAsync(
                    targetVersion.Value,
                    cancellationToken);

                ctx.Status("Building Archive Indexes...");
                await Task.WhenAll(
                    archiveIndexHandler.BuildArchiveIndexesAsync(cdnConfig, ctx, cancellationToken),
                    downloadFileHandler.ParseDownloadFileAsync(buildConfig, cancellationToken));

                // Start processing to determine which files need to be downloaded
                ctx.Status("Determining files to download...");
                await installFileHandler.HandleInstallFileAsync(
                    buildConfig,
                    archiveIndexHandler,
                    cdnConfig,
                    cancellationToken);
                await downloadFileHandler.HandleDownloadFileAsync(
                    archiveIndexHandler,
                    cdnConfig,
                    product,
                    cancellationToken);
                await patchLoader.HandlePatchesAsync(
                    buildConfig,
                    product,
                    cdnConfig,
                    cancellationToken);
            });

            _ansiConsole.LogMarkupLine("Retrieved product metadata", metadataTimer);

            // Actually start the download of any deferred requests
            var downloadSuccessful = await cdnRequestManager.DownloadQueuedRequestsAsync(_prefillSummaryResult, cancellationToken);

            // TODO I don't like the way that this has to be written just to get the debug output working.
            if (AppConfig.CompareAgainstRealRequests)
            {
                return await ComparisonUtil.CompareAgainstRealRequestsAsync(cdnRequestManager.allRequestsMade.ToList(), product);
            }
            if (_settings.SkipDownloads)
            {
                return null;
            }

            if (downloadSuccessful)
            {
                if (_settings.BeforeCommit != null) { await _settings.BeforeCommit(cancellationToken); }
                cancellationToken.ThrowIfCancellationRequested();
                var versionFilePath = $"{_settings.CacheDirectory}/prefilledVersion-{product.ProductCode}.txt";
                await CdnRequestManager.CommitAsync(versionFilePath, Encoding.UTF8.GetBytes(targetVersion.Value.versionsName),
                    (temporary, target) =>
                    {
                        void Commit()
                        {
                            if (_settings.CommitFile != null) { _settings.CommitFile(temporary, target); }
                            else { File.Move(temporary, target, overwrite: true); }
                        }
                        if (_settings.Run == null) { Commit(); }
                        else
                        {
                            _settings.Run.Commit(new AppDownloadInfo { AppId = product.ProductCode, Name = product.DisplayName },
                                Commit, cancellationToken);
                        }
                    }, cancellationToken);
                _prefillSummaryResult.Updated++;
            }
            else
            {
                _prefillSummaryResult.FailedApps++;
            }

            return null;
        }


        /// <summary>
        /// Checks to see if the previously prefilled version is up to date with the latest version on the CDN
        /// </summary>
        private bool IsProductUpToDate(TactProduct product, VersionsEntry latestVersion)
        {
            // Checking to see if a file has been previously prefilled
            var versionFilePath = $"{_settings.CacheDirectory}/prefilledVersion-{product.ProductCode}.txt";
            if (!File.Exists(versionFilePath))
            {
                return false;
            }

            // Checking to see if the game version previously prefilled is up to date with the latest version on the CDN.
            var lastPrefilledVersion = File.ReadAllText(versionFilePath);
            return latestVersion.versionsName == lastPrefilledVersion;
        }


        #region Select Apps

        public void SetAppsAsSelected(List<TuiAppInfo> tuiAppModels)
        {
            List<string> selectedAppIds = tuiAppModels.Where(e => e.IsSelected)
                                                    .Select(e => e.AppId)
                                                    .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListString));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!");
        }

        public static List<TactProduct> LoadPreviouslySelectedApps()
        {
            if (!File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return new List<TactProduct>();
            }

            return (JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListString)
                    ?? throw new InvalidDataException("Selected products must be a JSON array."))
                                 .Select(e => TactProduct.Parse(e))
                                 .ToList();
        }

        #endregion
    }
}
