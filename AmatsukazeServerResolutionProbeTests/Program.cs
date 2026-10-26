using Amatsukaze.Lib;
using Amatsukaze.Server;
using Amatsukaze.Server.Rest;
using Amatsukaze.Shared;
using NativeProgram = Amatsukaze.Lib.Program;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
}

static QueueItem NewItem() => new QueueItem
{
    SrcPath = "/synthetic/ts-probe.ts",
    Mode = Amatsukaze.Server.ProcMode.Test,
    StreamFormat = VideoStreamFormat.Unknown,
    State = QueueState.LogoPending
};

static NativeProgram NewProgram(int stream, int width, int height) => new NativeProgram
{
    ServiceId = 101,
    HasVideo = true,
    VideoPid = 0x100,
    Stream = stream,
    Width = width,
    Height = height
};

var unknown = NewItem();
var advertisedOnly = NewProgram((int)VideoStreamFormat.Unknown, 0, 0);
QueueManager.ApplyResolutionProbeResult(unknown, advertisedOnly,
    ResolutionProbeClassification.NativeUnknownResolution);
Check(unknown.State == QueueState.PreFailed &&
    unknown.FailReasonCode == ResolutionProbeClassification.UnknownResolutionCode,
    "PMT-advertised video without codec dimensions becomes unknown_resolution");
Check(!unknown.IsOneSeg && !ResolutionProbeClassification.CanAutomaticallyQueue(unknown),
    "unknown dimensions bypass one-seg rejection and automatic queueing");
Check(!ResolutionProbeClassification.AllowsQueueChange(unknown, Amatsukaze.Server.ChangeItemType.UpdateProfile) &&
    !ResolutionProbeClassification.AllowsQueueChange(unknown, Amatsukaze.Server.ChangeItemType.Duplicate) &&
    ResolutionProbeClassification.AllowsQueueChange(unknown, Amatsukaze.Server.ChangeItemType.ResetState),
    "profile update and duplicate stay held; explicit retry is allowed");
var ordinaryPreFailed = NewItem();
ordinaryPreFailed.State = QueueState.PreFailed;
Check(!ResolutionProbeClassification.AllowsQueueChange(ordinaryPreFailed, Amatsukaze.Server.ChangeItemType.UpdateProfile) &&
    !ResolutionProbeClassification.AllowsQueueChange(ordinaryPreFailed, Amatsukaze.Server.ChangeItemType.Duplicate) &&
    !ResolutionProbeClassification.AllowsQueueChange(ordinaryPreFailed, Amatsukaze.Server.ChangeItemType.ResetState),
    "unrelated PreFailed items keep the existing reset, update and duplicate hold");
QueueManager.ApplyResolutionProbeResult(unknown, advertisedOnly,
    ResolutionProbeClassification.NativeUnknownResolution);
Check(unknown.State == QueueState.PreFailed &&
    unknown.FailReasonCode == ResolutionProbeClassification.UnknownResolutionCode &&
    !ResolutionProbeClassification.CanAutomaticallyQueue(unknown),
    "reclassification is idempotent and leaves unknown video unqueued");

var exhausted = NewItem();
QueueManager.ApplyResolutionProbeResult(exhausted,
    NewProgram((int)VideoStreamFormat.Unknown, 0, 0),
    ResolutionProbeClassification.NativeReadBudgetExhausted);
Check(exhausted.State == QueueState.PreFailed &&
    exhausted.FailReasonCode == ResolutionProbeClassification.ReadBudgetExhaustedCode,
    "exhausted probe budget has its own stable reason");

foreach (var partial in new[] { (1920, 0), (0, 1080), (0, 0) })
{
    var item = NewItem();
    QueueManager.ApplyResolutionProbeResult(item,
        NewProgram((int)VideoStreamFormat.MPEG2, partial.Item1, partial.Item2),
        ResolutionProbeClassification.NativeUnknownResolution);
    Check(item.State == QueueState.PreFailed &&
        item.FailReasonCode == ResolutionProbeClassification.UnknownResolutionCode &&
        !item.IsOneSeg,
        "one-sided or missing dimensions remain unknown");
}

foreach (var low in new[] { (320, 1080), (1920, 260) })
{
    var item = NewItem();
    QueueManager.ApplyResolutionProbeResult(item,
        NewProgram((int)VideoStreamFormat.MPEG2, low.Item1, low.Item2),
        ResolutionProbeClassification.NativeResolved);
    Check(item.State == QueueState.PreFailed &&
        item.FailReasonCode == ResolutionProbeClassification.LowResolutionCode &&
        item.IsOneSeg,
        "only confirmed positive low dimensions become low_resolution");
}

var hd = NewItem();
QueueManager.ApplyResolutionProbeResult(hd,
    NewProgram((int)VideoStreamFormat.H264, 1920, 1080),
    ResolutionProbeClassification.NativeResolved);
Check(hd.State == QueueState.LogoPending && hd.FailReasonCode == null && !hd.IsOneSeg,
    "confirmed HD resolution continues through normal queue classification");

var roundTripItem = NewItem();
QueueManager.ApplyResolutionProbeResult(roundTripItem, advertisedOnly,
    ResolutionProbeClassification.NativeUnknownResolution);
var queueData = new QueueData { Items = new List<QueueItem> { roundTripItem } };
var dataContractSerializer = new DataContractJsonSerializer(typeof(QueueData));
using var serialized = new MemoryStream();
dataContractSerializer.WriteObject(serialized, queueData);
var dataContractJson = Encoding.UTF8.GetString(serialized.ToArray());
serialized.Position = 0;
var restored = (QueueData)dataContractSerializer.ReadObject(serialized);
Check(restored.Items.Single().FailReasonCode == ResolutionProbeClassification.UnknownResolutionCode,
    "DataContract persistence round-trips the stable reason code");

var apiJson = JsonSerializer.Serialize(roundTripItem,
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(apiJson.Contains("\"failReasonCode\":\"unknown_resolution\"", StringComparison.Ordinal),
    "web JSON API shape includes failReasonCode");

var wireItems = new[]
{
    NewPreFailedWireItem(ResolutionProbeClassification.UnknownResolutionCode),
    NewPreFailedWireItem(ResolutionProbeClassification.ReadBudgetExhaustedCode),
    NewPreFailedWireItem(ResolutionProbeClassification.LowResolutionCode)
}.Select(RestStateStore.ToQueueItemView).ToList();
var queueView = new QueueView
{
    Version = 7,
    Digest = "synthetic-preflight-v1",
    Items = wireItems,
    Counters = new QueueCounters { Active = 0, Encoding = 0, Complete = 0, Pending = 0, Failed = 3, Canceled = 0 },
    Filters = new QueueFilter()
};
var restOptions = new JsonSerializerOptions();
RestApiHost.ConfigureRestJsonOptions(restOptions);
var actualRestWire = JsonSerializer.SerializeToUtf8Bytes(queueView, restOptions);
Check(Encoding.UTF8.GetString(actualRestWire).Contains("\"state\":\"PreFailed\"", StringComparison.Ordinal) &&
    Encoding.UTF8.GetString(actualRestWire).Contains("\"failReasonCode\":\"unknown_resolution\"", StringComparison.Ordinal) &&
    Encoding.UTF8.GetString(actualRestWire).Contains("\"failReasonCode\":\"read_budget_exhausted\"", StringComparison.Ordinal) &&
    Encoding.UTF8.GetString(actualRestWire).Contains("\"failReasonCode\":\"low_resolution\"", StringComparison.Ordinal),
    "production REST projection and serializer expose all three preflight codes");
var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "queue-view-preflight.json");
if (File.Exists(fixturePath))
{
    var fixture = File.ReadAllBytes(fixturePath);
    Check(actualRestWire.SequenceEqual(fixture),
        "production REST serializer matches pinned queue-view fixture byte-for-byte");
}
Console.WriteLine("QUEUE_VIEW_WIRE_JSON " + Encoding.UTF8.GetString(actualRestWire));
if (args.Length == 2 && args[0] == "--write-wire-fixture")
{
    File.WriteAllBytes(args[1], actualRestWire);
    Console.WriteLine("WROTE_QUEUE_VIEW_FIXTURE");
}
Console.WriteLine("DATA_CONTRACT_JSON " + dataContractJson);
Console.WriteLine("API_JSON_HAS_FAIL_REASON_CODE true");

static QueueItem NewPreFailedWireItem(string reasonCode)
{
    var item = NewItem();
    item.Id = reasonCode == ResolutionProbeClassification.UnknownResolutionCode ? 101
        : reasonCode == ResolutionProbeClassification.ReadBudgetExhaustedCode ? 102 : 103;
    item.ServiceId = item.Id;
    item.ServiceName = "Synthetic service";
    item.EventName = "Synthetic event";
    item.ImageWidth = 0;
    item.ImageHeight = 0;
    if (reasonCode == ResolutionProbeClassification.UnknownResolutionCode)
    {
        QueueManager.ApplyResolutionProbeResult(item,
            NewProgram((int)VideoStreamFormat.Unknown, 0, 0),
            ResolutionProbeClassification.NativeUnknownResolution);
    }
    else if (reasonCode == ResolutionProbeClassification.ReadBudgetExhaustedCode)
    {
        QueueManager.ApplyResolutionProbeResult(item,
            NewProgram((int)VideoStreamFormat.Unknown, 0, 0),
            ResolutionProbeClassification.NativeReadBudgetExhausted);
    }
    else
    {
        QueueManager.ApplyResolutionProbeResult(item,
            NewProgram((int)VideoStreamFormat.MPEG2, 320, 1080),
            ResolutionProbeClassification.NativeResolved);
    }
    return item;
}
