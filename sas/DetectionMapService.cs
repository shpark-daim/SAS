using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sas;

public interface IDetectionMapService {
    bool GetDetectionMap(DetectionMapKey key, out DetectionMap? map);
    bool IsVehicleRegistered(string vehId);
    void UpdateVehicleRegistration(string vehId, string nextNode, ImmutableList<string> path);
    void UnRegisterVehicle(string vehId);
}

public class DetectionMapService : IDetectionMapService {
    private readonly IMapService _mapService;
    private readonly GeneralOptions _options;
    private readonly ILogger<DetectionMapService> _logger;

    public DetectionMapService(IMapService mapService, IOptions<GeneralOptions> generalOptions, ILogger<DetectionMapService> logger) {
        _mapService = mapService;
        _mapService.OnInitialized += OnMapServiceInitialized;
        _logger = logger;
        _options = generalOptions.Value;
        var channels = JsonSerializer.Deserialize<ChannelConfig[]>(
            File.ReadAllText(_options.ChannelConfigPath)!);

        var rois = JsonSerializer.Deserialize<RoiConfig[]>(
            File.ReadAllText(_options.RoiConfigPath))!.ToDictionary(r => r.Roi);

        _detectionMaps = [.. channels
            .SelectMany(c => c.Rois.Select(roiId =>
            {
                var eventType = roiId.StartsWith("CTT")
                    ? EventType.HandDetected
                    : roiId.StartsWith("ITR") ? EventType.PersonIntrusion : EventType.HelmetMissing;

                rois.TryGetValue(roiId, out var roiConfig);

                return new DetectionMap(
                    c.Channel,
                    roiId,
                    eventType,
                    roiConfig?.Segments ?? [],
                    roiConfig?.Ports ?? [],
                    roiConfig?.Robots ?? []
                );
            }))];
    }

    private DetectionMap[] _detectionMaps { get; set; } = [];
    private readonly HashSet<string> _registeredVehicles = [];
    private readonly Dictionary<int, (string Start, string End)> _segmentNodeCache = [];


    public bool GetDetectionMap(DetectionMapKey key, out DetectionMap? map) {
        map = _detectionMaps.FirstOrDefault(dm => dm.Key == key);
        return map is not null;
    }

    public bool IsVehicleRegistered(string vehId) => _registeredVehicles.Contains(vehId);

    public void UpdateVehicleRegistration(string vehId, string nextNode, ImmutableList<string> path) {
        var portIds = _mapService.GetPortIdsOnNode(nextNode);
        bool registered = false;

        foreach (var dm in _detectionMaps) {
            bool onSegment = dm.SegmentIds.Any(s => IsNodeMatch(s, nextNode));
            bool onPort = dm.PortIds.Any(p => portIds.Contains(p));

            if (onSegment || onPort) {
                dm.VehicleIds.Add(vehId);
                registered = true;
                _logger.LogInformation("Vehicle {VehId} registered on {NextNode} as {Channel}-{DetectionMap}", vehId, nextNode, dm.ChannelId, dm.RoI);
            }
            else {
                dm.VehicleIds.Remove(vehId);
            }
        }

        if (registered) _registeredVehicles.Add(vehId);
        else _registeredVehicles.Remove(vehId);
    }

    public void UnRegisterVehicle(string vehId) {
        foreach (var dm in _detectionMaps) {
            dm.VehicleIds.Remove(vehId);
            _logger.LogInformation("Vehicle {VehId} unregistered from {DetectionMap}", vehId, dm.RoI);
        }
        _registeredVehicles.Remove(vehId);
    }

    private void OnMapServiceInitialized(object? sender, EventArgs e) {
        InitSegmentNodeCache();
    }

    private void InitSegmentNodeCache() {
        foreach (var dm in _detectionMaps) {
            foreach (var segmentId in dm.SegmentIds) {
                if (_segmentNodeCache.ContainsKey(segmentId)) continue;
                try {
                    var start = _mapService.GetSegmentStartNode(segmentId).Id;
                    var end = _mapService.GetSegmentEndNode(segmentId).Id;
                    _segmentNodeCache[segmentId] = (start, end);
                }
                catch (KeyNotFoundException) { }
            }
        }
    }

    private bool IsNodeMatch(int segmentId, string nextNode) {
        return _segmentNodeCache.TryGetValue(segmentId, out var nodes) &&
               (nodes.Start == nextNode || nodes.End == nextNode);
    }
}

public record DetectionMap(string ChannelId, string RoI, EventType EventType, int[] SegmentIds, string[] PortIds, string[] RobotIds)
{
    public DetectionMapKey Key => new(ChannelId, RoI, EventType);
    public HashSet<string> VehicleIds { get; set; } = [];
}


public record DetectionMapKey(string ChannelId, string RoI, EventType EventType);

public record ChannelConfig(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("rois")] string[] Rois
);

public record RoiConfig(
    [property: JsonPropertyName("roi")] string Roi,
    [property: JsonPropertyName("ports")] string[] Ports,
    [property: JsonPropertyName("segments")] int[] Segments,
    [property: JsonPropertyName("robots")] string[] Robots
);