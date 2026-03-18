using Daim.Xms.GraphQLClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sas;

public interface IConnectionService {
    event Action<ScpStatus>? OnStatusReceived;
    public bool IsConnected { get; }

    public Task ModifyPort(string id, bool disabled, CancellationToken ct);
    public Task ModifySegment(int id, bool disabled, CancellationToken ct);
    public Task EstopVehicle(string[] vids);
    public Task ResetVehicle(string[] vids);
    public Task AutoVehicle(string[] vids);

    public Task UpdateEquipment(string manipulatorId, bool isPause);
}

public class ConnectionService : IConnectionService, IHostedService {
    private readonly GeneralOptions _options;
    private readonly IMqttClient _mqttClient;
    private readonly IXmsClient _xmsClient;
    private readonly HttpClient _xflowClient;
    private readonly ILogger<ConnectionService> _logger;
    public ConnectionService(IXmsClient xmsClient, IHttpClientFactory clientFactory, ILogger<ConnectionService> logger, IOptions<GeneralOptions> generalOptions) {
        _logger = logger;
        _options = generalOptions.Value;
        _mqttClient = new MqttClientFactory().CreateMqttClient();
        _mqttClient.ConnectedAsync += MqttCientConnected;
        _mqttClient.DisconnectedAsync += MqttClientDisConnected;
        _mqttClient.ApplicationMessageReceivedAsync += OnClientMsgReceived;

        _xmsClient = xmsClient;
        _xflowClient = clientFactory.CreateClient("xflow");
        ConfigureHttpClient();
    }

    public async Task StartAsync(CancellationToken cancellationToken) => await Connect(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public event Action<ScpStatus>? OnStatusReceived;
    public bool IsConnected => _mqttClient.IsConnected;

    #region MQTT
    private async Task MqttCientConnected(MqttClientConnectedEventArgs e) {
        var result = e.ConnectResult;
        if (result.ResultCode != MqttClientConnectResultCode.Success) {
            _logger.LogWarning("Mqtt fail to connect: {ResultCode}", result.ResultCode);
        }
        else {
            await _mqttClient.SubscribeAsync("nota/events/+/narrative");
        }
    }

    private async Task MqttClientDisConnected(MqttClientDisconnectedEventArgs e) {
        _logger.LogWarning("Mqtt disconneceted: {Reason}, {ReasonString}", e.Reason, e.ReasonString);
        await Task.Delay(_options.ReconnectInterval * 1000);
        await Connect();
    }

    public async Task Connect(CancellationToken ct = default) {
        _logger.LogInformation("Mqtt try to connect ({MqttBrokerIp}:{MqttBrokerPort}) ...", "localhost", 1883);
        var mqttClientOptions = new MqttClientOptionsBuilder().WithTcpServer("localhost", 1883).Build();
        try {
            await _mqttClient.ConnectAsync(mqttClientOptions, ct);
        }
        catch (MQTTnet.Exceptions.MqttCommunicationException e) {
            _logger.LogWarning("{ExMsg}", e.Message);
        }
    }

    private Task OnClientMsgReceived(MqttApplicationMessageReceivedEventArgs e) {
        var message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
        var topic = e.ApplicationMessage.Topic;
        var status = JsonSerializer.Deserialize<ScpStatus>(message, s_jsonOptions);
        if (status != null) {
            _logger.LogInformation("MqttRecv: {Topic} {@Status}", topic, status);
            OnStatusReceived?.Invoke(status);
        }
        else {
            _logger.LogError("Failed to deserialize event from payload: {payload}", message);
        }
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new NanosecondsDateTimeOffsetConverter() }
    };
    #endregion MQTT

    #region xflowClient
    public async Task UpdateEquipment(string manipulatorId, bool isPause) {
        var dto = new EquipmentUnitCommandDto() {
            EqpUnitId = manipulatorId,
            MachineId = _eqpMachine[manipulatorId]
        };

        var command = isPause ? "pause" : "resume";
        var endpoint = $"/api/EquipmentUnit/{dto.EqpUnitId}/{command}";
        try {
            var response = await _xflowClient.PostAsJsonAsync(endpoint, dto);
            if (response.IsSuccessStatusCode) {
                _logger.LogInformation("Request successful: {StatusCode}", response.StatusCode);
            }
            else {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Request failed: {StatusCode} {errorcontent}", response.StatusCode, errorContent);
            }
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error occurred while updating eqpunit {EqpUnit}", manipulatorId);
        }
    }

    private readonly Dictionary<string, string> _eqpMachine = new()
    {
        { "EQ1", "EQP101" },
        { "EQ2", "EQP102" },
        { "EQ3", "EQP103" },
        { "EQ4", "EQP104" }
    };

    private void ConfigureHttpClient() {
        var baseUrl = _options.XflowUrl;
        if (!string.IsNullOrEmpty(baseUrl)) {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) {
                _xflowClient.BaseAddress = uri;
            }
            else {
                throw new InvalidOperationException($"Invalid BaseUrl: {baseUrl}");
            }
        }
        else {
            throw new InvalidOperationException("CoreService:BaseUrl not found in configuration");
        }

        // 공통 헤더 추가
        _xflowClient.DefaultRequestHeaders.Accept.Clear();
        _xflowClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }
    #endregion xflowClient

    #region xmsClient
    public async Task ModifyPort(string id, bool disabled, CancellationToken ct) {
        _logger.LogInformation("ModifyPort: Id={Id}, Disabled={Disabled}", id, disabled);
        await _xmsClient.ModifyPort.ExecuteAsync(new ModifyPortInput {
            Id = id,
            Disabled = disabled,
        }, ct);
    }

    public async Task ModifySegment(int id, bool disabled, CancellationToken ct) {
        _logger.LogInformation("ModifySegment: Id={Id}, Disabled={Disabled}", id, disabled);
        await _xmsClient.ModifySegment.ExecuteAsync(new ModifySegmentInput {
            Id = id,
            Disabled = disabled,
        }, ct);
    }

    public async Task EstopVehicle(string[] vids) {
        _logger.LogInformation("EstopVehicle: Ids={Ids}", string.Join(", ", vids));
        await _xmsClient.CommandVehicle.ExecuteAsync(new CommandVehicleInput {
            Ids = vids,
            Command = VehicleCommand.Estop,
        });
    }

    public async Task ResetVehicle(string[] vids) {
        _logger.LogInformation("ResetVehicle: Ids={Ids}", string.Join(", ", vids));
        await _xmsClient.CommandVehicle.ExecuteAsync(new CommandVehicleInput {
            Ids = vids,
            Command = VehicleCommand.Reset,
        });
    }

    public async Task AutoVehicle(string[] vids) {
        _logger.LogInformation("AutoVehicle: Ids={Ids}", string.Join(", ", vids));
        await _xmsClient.CommandVehicle.ExecuteAsync(new CommandVehicleInput {
            Ids = vids,
            Command = VehicleCommand.Auto,
        });
    }
    #endregion xmsClient
}
