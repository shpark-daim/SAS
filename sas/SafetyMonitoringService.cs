using Daim.Xms.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace sas;

public interface ISafetyMonitoringService {
    Task WriteChannelAsync(object request, CancellationToken ct);
}

public class SafetyMonitoringService : BackgroundService, ISafetyMonitoringService {
    private readonly ILogger<SafetyMonitoringService> _logger;
    private readonly IConnectionService _connectionService;
    private readonly IDetectionMapService _detectionMapService;
    private readonly ISafetyMonitoringManager _safetyStateManager;
    private readonly IVehicleService _vehicleService;
    private readonly Channel<object> _eventChannel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly GeneralOptions _options;

    public SafetyMonitoringService(
        IConnectionService connectionService,
        IDetectionMapService detectionMapService,
        ISafetyMonitoringManager safetyStateManager,
        IVehicleService vehicleService,
        IOptions<GeneralOptions> generalOptions,
        ILogger<SafetyMonitoringService> logger) {
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.OnStatusReceived += async (e) => await WriteChannelAsync(e);
        _detectionMapService = detectionMapService;
        _safetyStateManager = safetyStateManager;
        _vehicleService = vehicleService;
        _vehicleService.VehicleChanged += VehicleChangedEventHandler;
        _options = generalOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _logger.LogInformation("SafetyMonitoringService ExecuteAsync started");
        while (!stoppingToken.IsCancellationRequested) {
            var eventBuffer = new Queue<object>();
            await foreach (var eventData in _eventChannel.Reader.ReadAllAsync(stoppingToken)) {
                eventBuffer.Enqueue(eventData);
                if (_eventChannel.Reader.TryPeek(out _)) continue;
                await HandleEvents(eventBuffer, stoppingToken);
            }
        }
    }

    private async void VehicleChangedEventHandler(object? sender, ChangedEvent<Vehicle> e) {
        try { await WriteChannelAsync(e); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to enqueue vehicle changed event"); }
    }

    public async Task WriteChannelAsync(object request, CancellationToken ct = default) {
        await _eventChannel.Writer.WriteAsync(request, ct);
    }

    private async Task HandleEvents(Queue<object> events, CancellationToken ct) {
        while (events.Count > 0) {
            var bufferedEvent = events.Dequeue();
            switch (bufferedEvent) {
                case ScpStatus e: await HandleScpStatus(e, ct); break;
                case ChangedEvent<Vehicle> e: HandleVehicleChangedEvent(e); break;
                default:
                    break;
            }
        }
    }

    private async Task HandleScpStatus(ScpStatus status, CancellationToken ct) {
        if (_options.ReceivableRoi) {
            _logger.LogInformation("Received SCP Status: [{EventType}] RoiId = {RoiId}, Status={Status}, EventId={EventId}, Timestamp={Timestamp}",
                status.EventType, status.RoiId, status.Status, status.EventId, status.Timestamp);
        } else {
            _logger.LogInformation("Received SCP Status: [{EventType}] Status={Status}, EventId={EventId}, Timestamp={Timestamp}",
                status.EventType, status.Status, status.EventId, status.Timestamp);
        }
        switch (status.Status) {
            case Status.NEW:
                var key = new DetectionMapKey(status.ChannelId, _options.ReceivableRoi ? status.RoiId : "OHT1", status.EventType);
                if (_detectionMapService.GetDetectionMap(key, out DetectionMap? dm) && dm is { }) {
                    var pausObjs = _safetyStateManager.OnDetected(dm);
                    foreach (var segment in pausObjs.SegmentIds) await _connectionService.ModifySegment(segment, true, ct);
                    foreach (var port in pausObjs.PortIds) await _connectionService.ModifyPort(port, true, ct);
                    await _connectionService.EstopVehicle(pausObjs.VehicleIds);
                    foreach (var robot in pausObjs.RobotIds) await _connectionService.UpdateEquipment(robot, true);
                }
                break;
            case Status.IN_PROGRESS:
                break;
            case Status.FINISHED:
                var dmKey = new DetectionMapKey(status.ChannelId, _options.ReceivableRoi ? status.RoiId : "OHT1", status.EventType);
                if (_detectionMapService.GetDetectionMap(dmKey, out DetectionMap? dmObj) && dmObj is { })
                {
                    var resumeObjs = _safetyStateManager.OnCleared(dmObj);
                    foreach (var segment in resumeObjs.SegmentIds) await _connectionService.ModifySegment(segment, false, ct);
                    foreach (var port in resumeObjs.PortIds) await _connectionService.ModifyPort(port, false, ct);
                    await _connectionService.ResetVehicle(resumeObjs.VehicleIds);
                    await _connectionService.AutoVehicle(resumeObjs.VehicleIds);
                    foreach (var robot in resumeObjs.RobotIds) await _connectionService.UpdateEquipment(robot, false);
                }
                break;
        }
    }

    private void HandleVehicleChangedEvent(ChangedEvent<Vehicle> e) {
        var now = e.New;
        var old = e.Old;
        var vehId = now?.Id ?? old?.Id;
        if (vehId is null) return;

        if (now is null) {
            _detectionMapService.UnRegisterVehicle(vehId);
            return;
        }

        if (_detectionMapService.IsVehicleRegistered(vehId) && now.NextNodeId == old?.NextNodeId) return;
        _detectionMapService.UpdateVehicleRegistration(vehId, now.NextNodeId, now.Path);
    }
}