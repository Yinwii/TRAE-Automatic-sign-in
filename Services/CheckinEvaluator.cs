namespace TraeCheckin;

/// <summary>签到结果判定：解析本次签到实际获得的积分。</summary>
public static class CheckinEvaluator
{
    /// <summary>
    /// claim 接口响应只含 code/message，不返回本次所得积分；
    /// 单日奖励需从 status 接口（<see cref="CheckinStatus.credits"/>）读取。
    /// 仅当状态为已签到且 code=0 时，credits 才可信；否则视为无法取得奖励返回 0。
    /// </summary>
    public static double ResolveGainedCredits(CheckinStatus? statusAfterClaim)
        => statusAfterClaim != null && statusAfterClaim.code == 0 && statusAfterClaim.checked_in
            ? statusAfterClaim.credits
            : 0;
}