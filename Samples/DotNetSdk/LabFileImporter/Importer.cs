﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Humanizer;
using ServiceStack.Logging;

namespace LabFileImporter
{
    public class Importer
    {
        public Importer(ILog log, Context context)
        {
            Context = context;
            Log = log;
        }

        private Context Context { get; }
        private ILog Log { get; }

        public void Import()
        {
            Validate();

            var observations = LoadAll()
                .ToList();

            WriteObservationsAsCsv(observations);

            if (Context.MaximumObservations.HasValue)
            {
                observations = observations
                    .Take(Context.MaximumObservations.Value)
                    .ToList();
            }

            ImportObservationsToSamples(observations);
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(Context.ServerUrl) ^ string.IsNullOrWhiteSpace(Context.ApiToken))
                throw new ExpectedException(
                    $"You must specify both /{nameof(Context.ServerUrl)}= and /{nameof(Context.ApiToken)}= options to authenticate with AQUARIUS Samples");

            if (string.IsNullOrWhiteSpace(Context.ServerUrl) && string.IsNullOrWhiteSpace(Context.ApiToken) &&
                string.IsNullOrWhiteSpace(Context.CsvOutputPath))
                Context.CsvOutputPath = Path.Combine(ExeHelper.ExeDirectory,
                    $"Observations-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv");

            if (!Context.Files.Any())
                throw new ExpectedException(
                    $"No files to import. Try setting a /{nameof(Context.Files).Singularize()}= option.");

            if (!Context.Overwrite && File.Exists(Context.CsvOutputPath))
                throw new ExpectedException(
                    $"Can't overwrite existing file '{Context.CsvOutputPath}'. Try /{nameof(Context.Overwrite)}={true}");
        }

        private IEnumerable<ObservationV2> LoadAll()
        {
            return Context
                .Files
                .SelectMany(LoadAllObservations);
        }

        private IEnumerable<ObservationV2> LoadAllObservations(string path)
        {
            var observations = new LabFileLoader(Log, Context)
                .Load(path)
                .ToList();

            Log.Info($"Loaded {"observation".ToQuantity(observations.Count)} from '{path}'.");

            return observations;
        }

        private void WriteObservationsAsCsv(List<ObservationV2> observations)
        {
            if (string.IsNullOrEmpty(Context.CsvOutputPath))
                return;

            Log.Info($"Writing {"observation".ToQuantity(observations.Count)} to '{Context.CsvOutputPath}' ...");

            using (var writer = new StreamWriter(Context.CsvOutputPath))
            {
                new CsvWriter()
                    .WriteObservations(writer, observations);
            }
        }

        private void ImportObservationsToSamples(List<ObservationV2> observations)
        {
            if (string.IsNullOrWhiteSpace(Context.ServerUrl) || string.IsNullOrWhiteSpace(Context.ApiToken))
                return;

            Log.Info($"Connecting to {Context.ServerUrl} ...");

            using (var importClient = new ImportClient(Context))
            {
                var csvBytes = LoadObservationCsvBytes(observations);

                var filename = $"{ExeHelper.ExeNameAndVersion} Uploads.csv";

                Log.Info(Context.DryRun
                ? $"Dry-run of importing {"observation".ToQuantity(observations.Count)} ..."
                : $"Importing {"observation".ToQuantity(observations.Count)} ...");

                var stopwatch = Stopwatch.StartNew();

                var statusUrl = Context.DryRun
                    ? importClient.PostImportDryRunForStatusUrl(filename, csvBytes)
                    : importClient.PostImportForStatusUrl(filename, csvBytes);

                var status = importClient.GetImportStatusUntilComplete(statusUrl);
                var response = importClient.GetResult(status.ResultUri.ToString());

                var rowErrors = response
                    .ErrorImportItems
                    ?.SelectMany(errorItem => errorItem
                        .Errors
                        .SelectMany(errorContext => errorContext.Value.Select(error =>
                            $"Row {errorItem.RowId}: {error.ErrorMessage} '{error.ErrorFieldValue}'")))
                    .ToList();

                var distinctErrors = response
                    .ErrorImportItems
                    ?.SelectMany(errorItem => errorItem
                        .Errors
                        .SelectMany(errorContext => errorContext.Value.Select(error =>
                            $"{error.ErrorMessage} '{error.ErrorFieldValue}'")))
                    .Distinct()
                    .ToList();

                var emptyList = new List<string>();

                var errors = (Context.VerboseErrors ? rowErrors : distinctErrors) ?? emptyList;

                var summaryMessages = (response.ImportJobErrors?.Select(e => e.ErrorMessage) ?? emptyList)
                    .Concat(response
                        .SummaryReportText
                        .Split('\n'))
                    .Concat(errors.Count > Context.ErrorLimit
                        ? new[] {$"Showing first {Context.ErrorLimit} of {errors.Count} errors:"}
                        : new string[0])
                    .Concat(errors.Take(Context.ErrorLimit))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (response.ErrorCount > 0)
                {
                    summaryMessages.ForEach(Log.Error);
                    Log.Error($"Import completed with errors in {stopwatch.Elapsed.Humanize(2)}.");

                    throw new ExpectedException($"Invalid observations detected: {summaryMessages.FirstOrDefault()}");
                }

                summaryMessages.ForEach(Log.Info);
                Log.Info($"Import completed successfully in {stopwatch.Elapsed.Humanize(2)}.");
            }
        }
        
        private byte[] LoadObservationCsvBytes(List<ObservationV2> observations)
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var writer = new StreamWriter(memoryStream))
                {
                    new CsvWriter()
                        .WriteObservations(writer, observations);
                }

                return memoryStream.GetBuffer();
            }
        }
    }
}
