﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Covid19Radar.Model;
using Covid19Radar.Repository;
using Covid19Radar.Services.Logs;
using Xamarin.Essentials;

namespace Covid19Radar.Services
{
    public abstract class AbsExposureDetectionBackgroundService : IBackgroundService
    {
        private const string DIAGNOSIS_KEYS_DIR = "diagnosis_keys";

        private readonly IDiagnosisKeyRepository _diagnosisKeyRepository;
        private readonly AbsExposureNotificationApiService _exposureNotificationApiService;
        private readonly IExposureConfigurationRepository _exposureConfigurationRepository;
        private readonly ILoggerService _loggerService;
        private readonly IUserDataRepository _userDataRepository;

        private readonly IList<DiagnosisKeyServerConfiguration> _serverConfigurations = AppSettings.Instance.SupportedRegions.Select(
                    region => new DiagnosisKeyServerConfiguration()
                    {
                        ApiEndpoint = $"{AppSettings.Instance.CdnUrlBase}/{AppSettings.Instance.BlobStorageContainerName}",
                        Region = region
                    }).ToList();

        public AbsExposureDetectionBackgroundService(
            IDiagnosisKeyRepository diagnosisKeyRepository,
            AbsExposureNotificationApiService exposureNotificationApiService,
            IExposureConfigurationRepository exposureConfigurationRepository,
            ILoggerService loggerService,
            IUserDataRepository userDataRepository
            )
        {
            _diagnosisKeyRepository = diagnosisKeyRepository;
            _exposureNotificationApiService = exposureNotificationApiService;
            _exposureConfigurationRepository = exposureConfigurationRepository;
            _loggerService = loggerService;
            _userDataRepository = userDataRepository;
        }
          
        public abstract void Schedule();

        public async Task ExposureDetectionAsync(CancellationTokenSource cancellationTokenSource = null)
        {
            var cancellationToken = cancellationTokenSource?.Token ?? default(CancellationToken);

            foreach (var serverConfiguration in _serverConfigurations)
            {
                List<string> downloadedFileNameList = new List<string>();
                try
                {
                    var tmpDir = PrepareDir(serverConfiguration.Region);

                    var exposureConfiguration = await _exposureConfigurationRepository.GetExposureConfigurationAsync();
                    var diagnosisKeyEntryList = await _diagnosisKeyRepository.GetDiagnosisKeysListAsync(serverConfiguration, cancellationToken);

                    var lastProcessTimestamp = await _userDataRepository.GetLastProcessDiagnosisKeyTimestampAsync(serverConfiguration.Region);
                    var targetDiagnosisKeyEntryList = diagnosisKeyEntryList;

#if DEBUG
                    // Do nothing
#else
                    targetDiagnosisKeyEntryList = targetDiagnosisKeyEntryList
                        .Where(diagnosisKeyEntry => diagnosisKeyEntry.Created > lastProcessTimestamp).ToList();
#endif

                    if (targetDiagnosisKeyEntryList.Count() == 0)
                    {
                        _loggerService.Info($"No new diagnosis-key found on {serverConfiguration.ApiEndpoint}/{serverConfiguration.Region}.");
                        continue;
                    }

                    foreach (var diagnosisKeyEntry in targetDiagnosisKeyEntryList)
                    {
                        string filePath = await _diagnosisKeyRepository.DownloadDiagnosisKeysAsync(diagnosisKeyEntry, tmpDir, cancellationToken);

                        _loggerService.Debug($"URL {diagnosisKeyEntry.Url} have been downloaded.");

                        downloadedFileNameList.Add(filePath);
                    }

                    var downloadedFileNames = string.Join("\n", downloadedFileNameList);
                    _loggerService.Debug(downloadedFileNames);

                    await _exposureNotificationApiService.ProvideDiagnosisKeysAsync(
                        downloadedFileNameList,
                        exposureConfiguration,
                        cancellationTokenSource
                        );

                    // Save LastProcessDiagnosisKeyTimestamp after ProvideDiagnosisKeysAsync was succeeded.
                    var latestProcessTimestamp = targetDiagnosisKeyEntryList
                        .Select(diagnosisKeyEntry => diagnosisKeyEntry.Created)
                        .Max();

                    await _userDataRepository.SetLastProcessDiagnosisKeyTimestampAsync(serverConfiguration.Region, latestProcessTimestamp);

                }
                finally
                {
                    RemoveFiles(downloadedFileNameList);
                }
            }
        }

        private string PrepareDir(string region)
        {
            var cacheDir = FileSystem.CacheDirectory;

            var baseDir = Path.Combine(cacheDir, DIAGNOSIS_KEYS_DIR);
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            var dir = Path.Combine(baseDir, region);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        private void RemoveFiles(List<string> fileList)
        {
            foreach (var file in fileList)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception)
                {
                    _loggerService.Exception("Exception occurred", exception);
                }
            }
        }
    }
}
