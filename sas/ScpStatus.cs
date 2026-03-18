using System.ComponentModel;
using System.Text.Json.Serialization;

namespace sas;

public record ScpStatus(
    string Version,
    string Source,
    string EventId,
    string SiteId,
    string ChannelId,
    string RoiId,
    [property: JsonConverter(typeof(DescriptionEnumConverter<EventType>))]
    EventType EventType,
    [property: JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    Status Status,
    [property: JsonConverter(typeof(NanosecondsDateTimeOffsetConverter))]
    DateTimeOffset Timestamp
    ) {

    public virtual bool Equals(ScpStatus? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return EqualityComparer<string>.Default.Equals(Version, other.Version)
            && EqualityComparer<string>.Default.Equals(Source, other.Source)
            && EqualityComparer<string>.Default.Equals(EventId, other.EventId)
            && EqualityComparer<string>.Default.Equals(SiteId, other.SiteId)
            && EqualityComparer<string>.Default.Equals(ChannelId, other.ChannelId)
            && EqualityComparer<string>.Default.Equals(RoiId, other.RoiId)
            && EqualityComparer<EventType>.Default.Equals(EventType, other.EventType)
            && EqualityComparer<Status>.Default.Equals(Status, other.Status)
            && Timestamp.Equals(other.Timestamp);

    }

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(Source);
        hash.Add(EventId);
        hash.Add(SiteId);
        hash.Add(ChannelId);
        hash.Add(RoiId);
        hash.Add(EventType);
        hash.Add(Status);
        hash.Add(Timestamp);
        return hash.ToHashCode();
    }
}

public enum Status {
    NEW,
    IN_PROGRESS,
    FINISHED
}

public enum EventType {
    [Description("P-001")] HelmetMissing,
    [Description("U-001")] HandDetected,
    [Description("V-002")] PersonIntrusion
}