using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiStation.Protocol.Identifiers;

[JsonConverter(typeof(EnvironmentIdJsonConverter))]
public readonly record struct EnvironmentId(string Value)
{
    public static EnvironmentId New() => new(Guid.NewGuid().ToString("N"));

    public static EnvironmentId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(ClientIdJsonConverter))]
public readonly record struct ClientId(string Value)
{
    public static ClientId New() => new(Guid.NewGuid().ToString("N"));

    public static ClientId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(ProjectIdJsonConverter))]
public readonly record struct ProjectId(string Value)
{
    public static ProjectId New() => new(Guid.NewGuid().ToString("N"));

    public static ProjectId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(ThreadIdJsonConverter))]
public readonly record struct ThreadId(string Value)
{
    public static ThreadId New() => new(Guid.NewGuid().ToString("N"));

    public static ThreadId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(TerminalSessionIdJsonConverter))]
public readonly record struct TerminalSessionId(string Value)
{
    public static TerminalSessionId New() => new(Guid.NewGuid().ToString("N"));

    public static TerminalSessionId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(TurnIdJsonConverter))]
public readonly record struct TurnId(string Value)
{
    public static TurnId New() => new(Guid.NewGuid().ToString("N"));

    public static TurnId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(CommandIdJsonConverter))]
public readonly record struct CommandId(string Value)
{
    public static CommandId New() => new(Guid.NewGuid().ToString("N"));

    public static CommandId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(InteractionIdJsonConverter))]
public readonly record struct InteractionId(string Value)
{
    public static InteractionId New() => new(Guid.NewGuid().ToString("N"));

    public static InteractionId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(DraftIdJsonConverter))]
public readonly record struct DraftId(string Value)
{
    public static DraftId New() => new(Guid.NewGuid().ToString("N"));

    public static DraftId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(AttachmentIdJsonConverter))]
public readonly record struct AttachmentId(string Value)
{
    public static AttachmentId New() => new(Guid.NewGuid().ToString("N"));

    public static AttachmentId Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(ProjectionEpochJsonConverter))]
public readonly record struct ProjectionEpoch(string Value)
{
    public static ProjectionEpoch New() => new(Guid.NewGuid().ToString("N"));

    public static ProjectionEpoch Parse(string value) => new(IdentifierValidation.Require(value, nameof(value)));

    public override string ToString() => Value;
}

[JsonConverter(typeof(SequenceJsonConverter))]
public readonly record struct Sequence(long Value) : IComparable<Sequence>
{
    public static Sequence Initial => new(0);

    public Sequence Next() => new(checked(Value + 1));

    public int CompareTo(Sequence other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public static bool operator <(Sequence left, Sequence right) => left.Value < right.Value;

    public static bool operator >(Sequence left, Sequence right) => left.Value > right.Value;

    public static bool operator <=(Sequence left, Sequence right) => left.Value <= right.Value;

    public static bool operator >=(Sequence left, Sequence right) => left.Value >= right.Value;
}

internal static class IdentifierValidation
{
    public static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

internal abstract class StringIdentifierJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Parse(string value);

    protected abstract string Format(T value);

    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string {typeof(T).Name}.");
        }

        var value = reader.GetString();
        return value is null ? throw new JsonException($"Expected a non-null {typeof(T).Name}.") : Parse(value);
    }

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));
}

internal sealed class EnvironmentIdJsonConverter : StringIdentifierJsonConverter<EnvironmentId>
{
    protected override EnvironmentId Parse(string value) => EnvironmentId.Parse(value);

    protected override string Format(EnvironmentId value) => value.Value;
}

internal sealed class ClientIdJsonConverter : StringIdentifierJsonConverter<ClientId>
{
    protected override ClientId Parse(string value) => ClientId.Parse(value);

    protected override string Format(ClientId value) => value.Value;
}

internal sealed class ProjectIdJsonConverter : StringIdentifierJsonConverter<ProjectId>
{
    protected override ProjectId Parse(string value) => ProjectId.Parse(value);

    protected override string Format(ProjectId value) => value.Value;
}

internal sealed class ThreadIdJsonConverter : StringIdentifierJsonConverter<ThreadId>
{
    protected override ThreadId Parse(string value) => ThreadId.Parse(value);

    protected override string Format(ThreadId value) => value.Value;
}

internal sealed class TerminalSessionIdJsonConverter : StringIdentifierJsonConverter<TerminalSessionId>
{
    protected override TerminalSessionId Parse(string value) => TerminalSessionId.Parse(value);

    protected override string Format(TerminalSessionId value) => value.Value;
}

internal sealed class TurnIdJsonConverter : StringIdentifierJsonConverter<TurnId>
{
    protected override TurnId Parse(string value) => TurnId.Parse(value);

    protected override string Format(TurnId value) => value.Value;
}

internal sealed class CommandIdJsonConverter : StringIdentifierJsonConverter<CommandId>
{
    protected override CommandId Parse(string value) => CommandId.Parse(value);

    protected override string Format(CommandId value) => value.Value;
}

internal sealed class InteractionIdJsonConverter : StringIdentifierJsonConverter<InteractionId>
{
    protected override InteractionId Parse(string value) => InteractionId.Parse(value);

    protected override string Format(InteractionId value) => value.Value;
}

internal sealed class DraftIdJsonConverter : StringIdentifierJsonConverter<DraftId>
{
    protected override DraftId Parse(string value) => DraftId.Parse(value);

    protected override string Format(DraftId value) => value.Value;
}

internal sealed class AttachmentIdJsonConverter : StringIdentifierJsonConverter<AttachmentId>
{
    protected override AttachmentId Parse(string value) => AttachmentId.Parse(value);

    protected override string Format(AttachmentId value) => value.Value;
}

internal sealed class ProjectionEpochJsonConverter : StringIdentifierJsonConverter<ProjectionEpoch>
{
    protected override ProjectionEpoch Parse(string value) => ProjectionEpoch.Parse(value);

    protected override string Format(ProjectionEpoch value) => value.Value;
}

internal sealed class SequenceJsonConverter : JsonConverter<Sequence>
{
    public override Sequence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, Sequence value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
