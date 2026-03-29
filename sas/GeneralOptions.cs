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

    // Dwell time in seconds. FINISHED event is delayed by this duration.
    // If a NEW event arrives within the dwell window, the pending FINISHED is cancelled.
    // Set to 0 to disable (execute FINISHED immediately).
    public int DwellTime { get; set; } = 0;

    // 복구 완료(Auto 요청) 후 프로그램을 종료하기까지 대기 시간 (초 단위).
    // 0으로 설정하면 종료하지 않음.
    public int TimeToKillProgramAfterRecovery { get; set; } = 0;
}
