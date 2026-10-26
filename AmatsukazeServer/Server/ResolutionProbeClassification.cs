using Amatsukaze.Lib;
using System;

namespace Amatsukaze.Server
{
    /// <summary>
    /// nativeのTS解析結果をキューの解像度判定へ反映する。
    /// </summary>
    public static class ResolutionProbeClassification
    {
        public const int NativeResolved = 0;
        public const int NativeUnknownResolution = 1;
        public const int NativeReadBudgetExhausted = 2;

        public const string LowResolutionCode = "low_resolution";
        public const string UnknownResolutionCode = "unknown_resolution";
        public const string ReadBudgetExhaustedCode = "read_budget_exhausted";

        public static void Apply(QueueItem item, Program program, int probeStatus)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var hasKnownCodec = program != null && program.Stream >= (int)VideoStreamFormat.MPEG2 &&
                program.Stream <= (int)VideoStreamFormat.H265;
            var hasPositiveResolution = program != null && program.Width > 0 && program.Height > 0;

            item.StreamFormat = hasKnownCodec && hasPositiveResolution
                ? (VideoStreamFormat)program.Stream
                : VideoStreamFormat.Unknown;
            item.ImageWidth = program != null ? program.Width : 0;
            item.ImageHeight = program != null ? program.Height : 0;

            if (item.StreamFormat == VideoStreamFormat.Unknown)
            {
                item.State = QueueState.PreFailed;
                item.FailReasonCode = probeStatus == NativeReadBudgetExhausted
                    ? ReadBudgetExhaustedCode
                    : UnknownResolutionCode;
                item.FailReason = item.FailReasonCode == ReadBudgetExhaustedCode
                    ? "映像の形式・解像度を読み取り上限内で確認できませんでした。手動で確認してください"
                    : "映像の形式または解像度を確認できませんでした。手動で確認してください";
                return;
            }

            if (item.IsOneSeg)
            {
                item.State = QueueState.PreFailed;
                item.FailReasonCode = LowResolutionCode;
                item.FailReason = "映像が小さすぎます(" + item.ImageWidth + "," + item.ImageHeight + ")";
                return;
            }

            item.State = QueueState.LogoPending;
            item.FailReasonCode = null;
            item.FailReason = string.Empty;
        }

        public static bool IsResolutionReviewItem(QueueItem item)
        {
            return item != null && item.State == QueueState.PreFailed &&
                (item.FailReasonCode == LowResolutionCode ||
                 item.FailReasonCode == UnknownResolutionCode ||
                 item.FailReasonCode == ReadBudgetExhaustedCode);
        }

        public static bool CanAutomaticallyQueue(QueueItem item)
        {
            return item != null && (item.State == QueueState.LogoPending || item.State == QueueState.Queue);
        }

        public static bool AllowsQueueChange(QueueItem item, ChangeItemType changeType)
        {
            return item != null && (item.State != QueueState.PreFailed ||
                (changeType == ChangeItemType.ResetState && IsResolutionReviewItem(item)));
        }
    }
}
