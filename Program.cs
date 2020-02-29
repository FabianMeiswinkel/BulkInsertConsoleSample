using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace BulkInsertConsole
{
    internal class Program
    {
        private const string CosmosConnectionStringConfigName = "CosmosConnectionString";
        private const string TargetDatabaseConfigName = "TargetDatabase";
        private const string TargetContainerConfigName = "TargetContainer";

        private const int BatchSize = 10000;

        private static string connectionString = null;
        private static string databaseName = null;
        private static string containerName = null;

        private static ManualResetEventSlim ingestionProcessingResetEvent = new ManualResetEventSlim();

        private static int documentsIngested;

        [SuppressMessage(
            "Style",
            "IDE0060:Remove unused parameter",
            Justification = "Intentional keeping comman line args")]
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 1 || String.IsNullOrWhiteSpace(args[0]))
            {
                Console.WriteLine(
                    "SYNTAX ERROR: Correct syntax is 'BulkInsertConsole <FileToBeIngested>");

                return 2;
            }

            PopulateConfigOverridesToEnvironmentVariables();

            if (!TryInitializeConfig())
            {
                Console.WriteLine("Please fix config/environment variables an restart!");
                Console.WriteLine("Press any key to quit...");
                Console.ReadKey();

                return 1;
            }

            var logFileNameTemplate =
                $"Logs/Log_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}_" + "{Date}.txt";
            Console.WriteLine("LOGFILENAME TEMPLATE {0}", logFileNameTemplate);

            IServiceCollection serviceCollection = new ServiceCollection();
            InitializeServices(serviceCollection, logFileNameTemplate);

            ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
            ILoggerFactory loggerFactory = serviceProvider.GetService<ILoggerFactory>();

            serviceProvider.EnableCosmosMetricEventListeners(counterIntervalInSeconds: 5);

            using (var cancellationTokenSource = new CancellationTokenSource())
            using (var metricEventListener = new LoggingComosMonitoringMetricEventListener(
                    loggerFactory.CreateLogger("CosmosDB.Metrics")))
            {
                // Stop ingestion after 30 seconds for testing purposes
                //cancellationTokenSource.CancelAfter(30000);
                CancellationToken cancellationToken = cancellationTokenSource.Token;

                Console.WriteLine("Starting bulk ingestion...");

                // Offload bulk ingestion to thread pool thread
                DoBulkIngestionAsync(args[0], loggerFactory, cancellationToken);

                while (!ingestionProcessingResetEvent.IsSet)
                {
                    Console.WriteLine("Press Q to quit...");
                    ConsoleKeyInfo key = Console.ReadKey();

                    if (key.Key == ConsoleKey.Q)
                    {
                        cancellationTokenSource.Cancel();

                        Console.WriteLine("Aborting bulk ingestion...");
                        ingestionProcessingResetEvent.Wait();
                    }
                }

                Console.WriteLine("Finished - press any key to quit program...");
                Console.ReadKey();

                return 0;
            }
        }

        private static CosmosMonitoringMetricsEventListenerBase CreateDefaultMetricEventListener(
            IServiceProvider serviceProvider)
        {
            ILoggerFactory loggerFactory = serviceProvider.GetService<ILoggerFactory>();

            return new LoggingComosMonitoringMetricEventListener(
                    loggerFactory.CreateLogger("CosmosDB.Metrics"));
        }

        private static void InitializeServices(IServiceCollection serviceCollection, string logFileNameTemplate)
        {
            var levelOverrides = new Dictionary<string, LogLevel>()
            {
                { "CosmosDB.Metrics", LogLevel.Information },
            };

            // Enabling just a Console logger
            serviceCollection.AddLogging(builder => builder
              .AddConsole()
              .AddFile(logFileNameTemplate, minimumLevel: LogLevel.Trace, levelOverrides)

              // log only summary perf counter events and CPU usage
              .AddFilter<ConsoleLoggerProvider>("CosmosDB.Metrics", LogLevel.Information)

              // besides perf counter values only log warnings and errors
              .AddFilter<ConsoleLoggerProvider>("", LogLevel.Warning)
            );

            serviceCollection.AddSingleton<CosmosMonitoringMetricsEventListenerBase>(CreateDefaultMetricEventListener);
        }

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Intentionally handling all exceptions. " +
                "This is running on a thread pool thread. Exeption would crash the process.")]
        private static async void DoBulkIngestionAsync(
            string filePath,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            // Ensure ingestion runs on background thread...
            await Task.Yield();

            var watch = Stopwatch.StartNew();

            try
            {
                var clientOptions = new CosmosClientOptions()
                {
                    AllowBulkExecution = true,
                    ConnectionMode = ConnectionMode.Direct,
                    MaxRequestsPerTcpConnection = -1,
                    MaxTcpConnectionsPerEndpoint = -1,
                    ConsistencyLevel = ConsistencyLevel.Eventual,
                    MaxRetryAttemptsOnRateLimitedRequests = 999,
                    MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromHours(1),
                };

                // The monitoring handler ensures logging of request diagnostics for all requests made
                // by the CosmosClient instance and is also emitting metrics for 'RequestCount',
                // 'RequestCharge' and 'LatencyMs'
                var monitoringRequestHandler = new CosmosMonitoringRequestHandler(
                    connectionString.GetAccountEndpointFromConnectionString(),
                    loggerFactory.CreateLogger("CosmosDB.RequestDiagnostics"));
                clientOptions.CustomHandlers.Add(monitoringRequestHandler);

                using (var cosmosClient = new CosmosClient(connectionString, clientOptions))
                {
                    Database database =
                        await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName).IgnoreCapturedContext();

                    Console.WriteLine("Creating or Retrieving a container of 50000 RU/s container...");

                    ContainerResponse containerResponse = await database.DefineContainer(containerName, "/pk")
                            .WithIndexingPolicy()
                                .WithIndexingMode(IndexingMode.Consistent)
                                .WithIncludedPaths()
                                    .Path("/pk/?")
                                    .Attach()
                                .WithExcludedPaths()
                                    .Path("/*")
                                    .Attach()
                            .Attach()
                        .CreateIfNotExistsAsync(50000).IgnoreCapturedContext();

                    Container container = containerResponse.Container;

                    // Determines how many parallel threads try to ingest batches of BatchSize documents
                    // Usually roughly the number of physcial cores is a good starting point
                    // Increase this if your ingestion client isn't located in the same Azure region as the
                    // CosmosDB account and your client is powerful enough to handle the additional CPU load and
                    // memory pressure
                    using (var concurrencyThrottle = new SemaphoreSlim(Environment.ProcessorCount * 2))
                    using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var bs = new BufferedStream(fs))
                    using (var sr = new StreamReader(bs))
                    {
                        var eofReached = false;

                        var batchIngestionTasks = new List<Task>();

                        while (!eofReached && !cancellationToken.IsCancellationRequested)
                        {
                            // Ensure max concurrency limit to protect client form getting overloaded or running
                            // out of memory
                            await concurrencyThrottle.WaitAsync().IgnoreCapturedContext();

                            // retrieve the next BatchSize items from the
                            IReadOnlyCollection<PricePaidData> items =
                                await GetItemsForNextBatchAsync(sr).IgnoreCapturedContext();

                            if (items.Count == 0)
                            {
                                break;
                            }
                            else if (items.Count < BatchSize)
                            {
                                eofReached = true;
                            }

                            // offload actual ingestion to another thread pool thread
                            batchIngestionTasks.Add(
                                IngestBatchAsync(container, items, concurrencyThrottle, cancellationToken));
                        }

                        await Task.WhenAll(batchIngestionTasks).IgnoreCapturedContext();
                    }
                }
            }
            catch (Exception unhandledException)
            {
                Console.WriteLine("UNHANDLED EXCEPTION: {0}", unhandledException);
            }
            finally
            {
                watch.Stop();
                Console.WriteLine(
                    "INEGSTION COMPLETED in {0} ms- Press any key to proceed.",
                    watch.Elapsed.TotalMilliseconds.ToString("#,###,###,###,##0.00000", CultureInfo.InvariantCulture));

                ingestionProcessingResetEvent.Set();
            }
        }

        private static async Task IngestBatchAsync(
            Container container,
            IReadOnlyCollection<PricePaidData> items,
            SemaphoreSlim concurrencyThrottle,
            CancellationToken cancellationToken)
        {
            try
            {
                var ingestionTasks = new List<Task>(BatchSize);

                foreach (PricePaidData item in items)
                {
                    ingestionTasks.Add(
                        container.UpsertItemAsync<PricePaidData>(
                            item,
                            new PartitionKey(item.pk),
                            requestOptions: null,
                            cancellationToken)
                        .ContinueWith(
                            (Task<ItemResponse<PricePaidData>> task) =>
                            {
                                if (!task.IsCompletedSuccessfully)
                                {
                                    AggregateException innerExceptions = task.Exception.Flatten();
                                    var cosmosException =
                                        innerExceptions.InnerExceptions.FirstOrDefault(
                                            innerEx => innerEx is CosmosException) as CosmosException;
                                    Console.WriteLine(
                                        $"Item {item.Transaction_unique_identifieroperty} failed with " +
                                        $"status code {cosmosException.StatusCode}");
                                }
                                else
                                {
                                    Interlocked.Increment(ref documentsIngested);
                                }
                            },
                            TaskScheduler.Default));
                }

                await Task.WhenAll(ingestionTasks).IgnoreCapturedContext();

                Console.WriteLine(
                    "STATUS: {0} documents ingested successfully",
                    documentsIngested.ToString("#,###,###,##0", CultureInfo.InvariantCulture));
            }
            finally
            {
                concurrencyThrottle.Release();
            }
        }

        private static bool TryInitializeConfig()
        {
            connectionString = Environment.GetEnvironmentVariable(
                CosmosConnectionStringConfigName,
                EnvironmentVariableTarget.Process);

            if (String.IsNullOrWhiteSpace(connectionString))
            {
                Console.WriteLine($"ERROR - Environment variable '{CosmosConnectionStringConfigName}' " +
                    "not set to a valid connection string for the CosmosDB account.");

                return false;
            }

            databaseName = Environment.GetEnvironmentVariable(
                TargetDatabaseConfigName,
                EnvironmentVariableTarget.Process);

            if (String.IsNullOrWhiteSpace(databaseName))

            {
                Console.WriteLine($"ERROR - Environment variable '{TargetDatabaseConfigName}' " +
                    "not set to a valid database name.");

                return false;
            }

            containerName = Environment.GetEnvironmentVariable(
                TargetContainerConfigName,
                EnvironmentVariableTarget.Process);

            if (String.IsNullOrWhiteSpace(containerName))

            {
                Console.WriteLine($"ERROR - Environment variable '{TargetContainerConfigName}' " +
                    "not set to a valid container name.");

                return false;
            }

            return true;
        }

        private static void PopulateConfigOverridesToEnvironmentVariables()
        {
            IConfigurationRoot settings = new ConfigurationBuilder()
                .AddJsonFile("settings.json", false)
                .Build();

            foreach (KeyValuePair<string, string> item in settings.AsEnumerable())
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value, EnvironmentVariableTarget.Process);
            }
        }

        private static async Task<IReadOnlyCollection<PricePaidData>> GetItemsForNextBatchAsync(StreamReader sr)
        {
            var lstPricedata = new List<PricePaidData>();
            string line;
            while ((line = await sr.ReadLineAsync().IgnoreCapturedContext()) != null &&
                lstPricedata.Count < BatchSize)
            {
                var pricedataArray = line.Split(',');
                var ppd = new PricePaidData()
                {
                    Transaction_unique_identifieroperty = pricedataArray[0],
                    Price = pricedataArray[1].Replace("\"", "", StringComparison.Ordinal),
                    Date_of_Transfer = pricedataArray[2].Replace("\"", "", StringComparison.Ordinal),
                    Postcode = pricedataArray[3].Replace("\"", "", StringComparison.Ordinal),
                    PropertyType = pricedataArray[4].Replace("\"", "", StringComparison.Ordinal),
                    isNew = pricedataArray[5].Replace("\"", "", StringComparison.Ordinal),
                    Duration = pricedataArray[6].Replace("\"", "", StringComparison.Ordinal),
                    PAON = pricedataArray[7].Replace("\"", "", StringComparison.Ordinal),
                    SAON = pricedataArray[8].Replace("\"", "", StringComparison.Ordinal),
                    Street = pricedataArray[9].Replace("\"", "", StringComparison.Ordinal),
                    Locality = pricedataArray[10].Replace("\"", "", StringComparison.Ordinal),
                    Town_City = pricedataArray[11].Replace("\"", "", StringComparison.Ordinal),
                    District = pricedataArray[12].Replace("\"", "", StringComparison.Ordinal),
                    County = pricedataArray[13].Replace("\"", "", StringComparison.Ordinal),
                    PPD_Category = pricedataArray[14].Replace("\"", "", StringComparison.Ordinal),
                    Record_Status = pricedataArray[15].Replace("\"", "", StringComparison.Ordinal),
                    pk = pricedataArray[0]
                };

                lstPricedata.Add(ppd);
            }

            return lstPricedata;
        }
    }
}
