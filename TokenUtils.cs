namespace TraeCheckin;

/// <summary>
/// 登录 token 判定工具。
/// 用于避免登录框误读 localStorage 中残留的失效 token。
/// </summary>
public static class TokenUtils
{
    /// <summary>
    /// 判断是否接受当前读到的 token。
    /// 仅当「当前 token 有效（非空且长度足够）」且「与初始 token 不同」时返回 true，
    /// 即真正重新登录产生了新 token。
    /// </summary>
    public static bool ShouldAcceptNewToken(string? initialToken, string? currentToken)
    {
        if (string.IsNullOrEmpty(currentToken) || currentToken.Length < 40) return false;
        return !string.Equals(initialToken, currentToken, StringComparison.Ordinal);
    }
}
