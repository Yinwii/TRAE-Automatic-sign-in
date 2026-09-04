namespace TraeCheckin;

/// <summary>签到结果判定：解析本次签到实际获得的积分。</summary>
public static class CheckinEvaluator
{
    /// <summary>
    /// claim 接口响应只含 code/message，不返回本次所得积分；
    /// 单日奖励需从 status 接口读取，且包含基础签到 credits 与连续/额外
    /// 签到 extra_credits（如基础 150 + 连签 50 = 200），二者都应计入本次所得。
    /// 仅当状态为已签到且 code=0 时数值可信；否则返回 0。
    /// </summary>
    public static double ResolveGainedCredits(CheckinStatus? statusAfterClaim)
        => statusAfterClaim != null && statusAfterClaim.code == 0 && statusAfterClaim.checked_in
            ? statusAfterClaim.credits + statusAfterClaim.extra_credits
            : 0;
}