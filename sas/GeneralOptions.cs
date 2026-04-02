namespace sas;

public class GeneralOptions {
    public const string Section = "General";

    public string XflowUrl { get; set; } = "http://localhost:8585";

    public string XmsUrl { get; set; } = "http://localhost:8787/graphql";

    // MQTT
    public int ReconnectInterval { get; set; } = 5;

    public bool ReceivableRoi { get; set; } = false;

    public string ChannelConfigPath { get; set; } = "config/channels_default.json";

    public string RoiConfigPath { get; set; } = "config/rois_default.json";

    // HelmetMissing/PersonIntrusion NEW 이벤트 페어링 대기 시간 (in seconds)
    // 0 : 페어링하지 않음.
    public int PresetDwellTime { get; set; } = 3;

    // FINISHED 이벤트 지연 시간 (in seconds)
    // 0 : 즉시 재개.
    public int RecoveryDwellTime { get; set; } = 10;

    // 복구 요청 후 프로그램을 종료하기까지 대기 시간 (in seconds)
    // 0 : 종료하지 않음.
    public int TimeToKillProgramAfterRecovery { get; set; } = 0;
}
