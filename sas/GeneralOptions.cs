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
}
