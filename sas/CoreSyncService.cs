using Daim.Xms.Common;
using Daim.Xms.GraphQLClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace sas;

public class CoreSyncService : BackgroundService {
    private readonly ILogger<CoreSyncService> _logger;
    private readonly IXmsClient _client;
    private readonly IVehicleService _vehicleService;
    private readonly IMapService _mapService;
    private readonly ISeedAndEventReporter[] _reporters;
    public CoreSyncService(
        ILogger<CoreSyncService> logger,
        IXmsClient client,
        IVehicleService vehicleService,
        IMapService mapService) {
        _logger = logger;
        _client = client;
        _vehicleService = vehicleService;
        _mapService = mapService;
        // mapInfo, node, segment, port
        _mapService.Initialize(false, true, true, true);
        _reporters = CreateReporters();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var reporterTasks = Array.Empty<Task>();
        CancellationTokenSource? cts = null;

        while (!stoppingToken.IsCancellationRequested) {
            try {
                using (cts) cts?.Cancel();
                await Task.WhenAll(reporterTasks);
            }
            catch (OperationCanceledException) {
                // no op
            }
            catch (HttpRequestException ex) {
                _logger.LogWarning("{ExMsg}, Retry to initialize...", ex.Message);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "reporter faulted");
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _logger.LogWarning("begin init");
            _vehicleService.Reset();
            reporterTasks = [.. _reporters.Select(x => x.RunAsync(cts.Token))];
            try {
                _logger.LogInformation("Sync start");
                await Task.WhenAll(_reporters.Select(x => x.InitializeTask.AsTask()));

                _logger.LogInformation("Sync end");
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException) {
                continue;
            }
            catch (Exception ex) {
                _logger.LogError("{ExMsg}, reporters have failed to initialize", ex.Message);
                continue;
            }
            _logger.LogWarning("end init");

            // subscription이 중단되면 데이터 동기화가 불가능하므로 루프를 재시작한다.
            await Task.WhenAny(reporterTasks);
            _logger.LogError("Disconnected");
        }
    }

    private ISeedAndEventReporter[] CreateReporters() => [
        _client.VehicleChanged.Watch(true)
            .ToReporter(x => x.Data!.VehicleChanged.Sequence == 0)
            .Select(x => x.Data!.ToChangedEvent())
            .Where(x => (x.New ?? x.Old)!.Model == "OHT")
            .OnSeed(x => _vehicleService.SeedVehicle(x.New))
            .OnSeedFinished(x => _vehicleService.SeedVehicle(null))
            .OnEventAwait(async x => {
                switch ((x.New, x.Old)) {
                case (not null, null): _vehicleService.CreateVehicle(x.New); break;
                case (not null, not null): _vehicleService.UpdateVehicle(x.New); break;
                case (null, not null): _vehicleService.RemoveVehicle(x.Old.Id, out _); break;
                default: throw new ApplicationException();
                }
                await _vehicleService.SaveChangesAsync();
            }),
        _client.NodeChanged.Watch(true)
            .ToReporter(x => x.Data!.NodeChanged.Sequence == 0)
            .Select(x => x.Data!.ToChangedEvent())
            .OnSeed(x => _mapService.SeedNode(x.New))
            .OnSeedFinished(x => _mapService.SeedNode(null)),
        _client.SegmentChanged.Watch(true)
            .ToReporter(x => x.Data!.SegmentChanged.Sequence == 0)
            .Select(x => x.Data!.ToChangedEvent())
            .OnSeed(x => _mapService.SeedSegment(x.New))
            .OnSeedFinished(x => _mapService.SeedSegment(null)),
        _client.PortChanged.Watch(true)
            .ToReporter(x => x.Data!.PortChanged.Sequence == 0)
            .Select(x => x.Data!.ToChangedEvent())
            .OnSeed(x => _mapService.SeedPort(x.New))
            .OnSeedFinished(x => _mapService.SeedPort(null)),
    ];
}
