namespace TraeCheckin;

/// <summary>签到结果判定：区分「本次新签到」与「已在云端完成签到」。</summary>
public static class CheckinEvaluator
{
    /// <summary>
    /// claim 返回 code=0 且已签到且 credits&lt;=0 时，说明今天已在（云端）完成签到，
    /// 本次并非新获得积分，不应再记录 0 积分。
    /// </summary>
    public static bool IsAlreadyCheckedIn(CheckinStatus result)
        => result != null && result.code == 0 && result.checked_in && result.credits <= 0;
}
