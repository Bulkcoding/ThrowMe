using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThrowMe.Network;

/// <summary>
/// 릴레이 프로토콜 DTO. 서버(<c>server/slimey-relay/src/protocol.ts</c>)와 1:1 대응.
/// 좌표는 절대값을 보내지 않고 엣지 파라미터 t + 엣지 기준 속도만 담는다(해상도/DPI 무관).
/// </summary>
public static class RelayProtocol
{
    public const int Version = 1;
}

/// <summary>메시지 타입 상수(서버 MessageType 과 일치).</summary>
public static class MsgType
{
    public const string Hello = "HELLO";
    public const string Welcome = "WELCOME";
    public const string Presence = "PRESENCE";
    public const string RoomConfig = "ROOM_CONFIG";
    public const string SetOrder = "SET_ORDER";
    public const string TransferHost = "TRANSFER_HOST";
    public const string SetTheme = "SET_THEME";
    public const string RoomStyle = "ROOM_STYLE";
    public const string Handoff = "HANDOFF";
    public const string Ack = "ACK";
    public const string HandoffResult = "HANDOFF_RESULT";
    public const string Heartbeat = "HEARTBEAT";
    public const string Error = "ERROR";
}

/// <summary>모든 메시지를 감싸는 봉투. 서버가 <see cref="To"/> 로 라우팅한다.</summary>
public sealed class Envelope
{
    [JsonPropertyName("v")] public int V { get; set; } = RelayProtocol.Version;
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("roomId")] public string? RoomId { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    /// <summary>타입별 페이로드. 수신 시 <see cref="RelayJson.DataAs{T}"/> 로 역직렬화.</summary>
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

public sealed class HelloData
{
    [JsonPropertyName("secret")] public string Secret { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "?";
}

public sealed class WelcomeData
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    [JsonPropertyName("links")] public List<EdgeLinkDto> Links { get; set; } = new();
    [JsonPropertyName("nodes")] public List<NodePresenceDto> Nodes { get; set; } = new();
    /// <summary>방장(방을 처음 만든 노드). 배치 결정 권한.</summary>
    [JsonPropertyName("host")] public string? Host { get; set; }
    /// <summary>파티 순서(좌 → 우).</summary>
    [JsonPropertyName("order")] public List<string> Order { get; set; } = new();
    /// <summary>방 공통 테마(방장 지정). null/빈값 = 각자 설정 유지.</summary>
    [JsonPropertyName("theme")] public string? Theme { get; set; }
}

public sealed class NodePresenceDto
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("hasBall")] public bool HasBall { get; set; }
}

public sealed class PresenceData
{
    [JsonPropertyName("nodes")] public List<NodePresenceDto> Nodes { get; set; } = new();
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("order")] public List<string> Order { get; set; } = new();
    [JsonPropertyName("theme")] public string? Theme { get; set; }
}

/// <summary>파티 순서 변경(방장만).</summary>
public sealed class SetOrderData
{
    [JsonPropertyName("order")] public List<string> Order { get; set; } = new();
}

/// <summary>방장 위임(방장만).</summary>
public sealed class TransferHostData
{
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

/// <summary>방 공통 테마 지정(방장만).</summary>
public sealed class SetThemeData
{
    [JsonPropertyName("theme")] public string Theme { get; set; } = "";
}

/// <summary>
/// 방장의 겉모습을 방 전체에 그대로 적용(방장만). 테마·가중치·커스텀 이미지를 함께 담는다.
/// 서버는 저장하지 않고 중계만 하므로, 새로 들어온 사람에게는 방장이 다시 보낸다.
/// </summary>
public sealed class RoomStyleData
{
    [JsonPropertyName("skin")] public string Skin { get; set; } = "";
    [JsonPropertyName("throwPower")] public double ThrowPower { get; set; }
    [JsonPropertyName("restitution")] public double Restitution { get; set; }
    [JsonPropertyName("softness")] public double Softness { get; set; }
    [JsonPropertyName("slimeSize")] public double SlimeSize { get; set; }
    [JsonPropertyName("skinImageEnabled")] public bool SkinImageEnabled { get; set; }
    [JsonPropertyName("skinImageScale")] public double SkinImageScale { get; set; }
    /// <summary>이미지가 속한 테마 이름(없으면 이미지 없음).</summary>
    [JsonPropertyName("imageSkin")] public string? ImageSkin { get; set; }
    /// <summary>커스텀 이미지(base64 PNG). 없으면 null.</summary>
    [JsonPropertyName("imagePng")] public string? ImagePng { get; set; }
}

public sealed class EdgeLinkDto
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("fromEdge")] public string FromEdge { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("toEdge")] public string ToEdge { get; set; } = "";
    [JsonPropertyName("flip")] public bool Flip { get; set; }
}

public sealed class RoomConfigData
{
    [JsonPropertyName("links")] public List<EdgeLinkDto> Links { get; set; } = new();
}

/// <summary>공 넘김 페이로드(LAN 설계 6절 필드 재사용).</summary>
public sealed class HandoffData
{
    [JsonPropertyName("handoffId")] public string HandoffId { get; set; } = "";
    [JsonPropertyName("viaLink")] public string ViaLink { get; set; } = "";
    [JsonPropertyName("edgeParam")] public double EdgeParam { get; set; }
    [JsonPropertyName("normalSpeed")] public double NormalSpeed { get; set; }
    [JsonPropertyName("tangentSpeed")] public double TangentSpeed { get; set; }
    [JsonPropertyName("angularVelocity")] public double AngularVelocity { get; set; }
    [JsonPropertyName("surfaceSpin")] public double SurfaceSpin { get; set; }
    [JsonPropertyName("surfaceSpinAxisDeg")] public double SurfaceSpinAxisDeg { get; set; }
    [JsonPropertyName("spinAngle")] public double SpinAngle { get; set; }
}

public sealed class AckData
{
    [JsonPropertyName("handoffId")] public string HandoffId { get; set; } = "";
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
}

public sealed class HandoffResultData
{
    [JsonPropertyName("handoffId")] public string HandoffId { get; set; } = "";
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ErrorData
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>릴레이 JSON 직렬화 공용 설정·헬퍼.</summary>
public static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Envelope env) => JsonSerializer.Serialize(env, Options);

    public static Envelope? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Envelope>(json, Options); }
        catch { return null; }
    }

    /// <summary>봉투의 <c>data</c> 를 지정 타입으로 역직렬화. 실패 시 null.</summary>
    public static T? DataAs<T>(this Envelope env) where T : class
    {
        if (env.Data is not JsonElement el) return null;
        try { return el.Deserialize<T>(Options); }
        catch { return null; }
    }

    /// <summary>임의 페이로드 객체를 <see cref="JsonElement"/> 로 감싸 봉투 data 에 넣는다.</summary>
    public static JsonElement ToElement(object payload) =>
        JsonSerializer.SerializeToElement(payload, Options);
}
