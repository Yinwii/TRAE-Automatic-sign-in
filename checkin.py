#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Trae 每日签到脚本（GitHub Actions 版）

原理：
  Trae 网页端的 JWT 只有 8 小时有效期，但真正的会话凭证是 HttpOnly Cookie
  `X-Cloudide-Session`（约 14 天有效）。本脚本通过该 Cookie 调用
  `GetUserToken` 接口换取全新 JWT，再用新 JWT 执行每日签到。

依赖：仅标准库，无第三方依赖。

环境变量：
  TRAE_SESSION   必填。X-Cloudide-Session 的 Cookie 值（登录后从浏览器抓取）
  TRAE_DEVICE_ID 选填。x-device-id 风控值，16 位数字；缺省随机生成（实测不敏感）

用法：
  python checkin.py
"""

import json
import os
import random
import sys
import urllib.request

BASE = "https://api.trae.cn"


def _post(path, headers, body=""):
    req = urllib.request.Request(BASE + path, data=body.encode("utf-8"), headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.status, resp.read().decode("utf-8", errors="replace")


def get_token(session: str) -> str:
    """用 X-Cloudide-Session Cookie 换取全新 JWT。"""
    headers = {
        "Cookie": "X-Cloudide-Session=" + session,
        "Referer": "https://www.trae.cn/",
        "Origin": "https://www.trae.cn",
        "User-Agent": "TraeCheckin/1.0",
        "Accept": "application/json, text/plain, */*",
    }
    status, text = _post("/cloudide/api/v3/common/GetUserToken", headers)
    data = json.loads(text)
    token = (data.get("Result") or {}).get("Token")
    if status != 200 or not token:
        raise RuntimeError("GetUserToken 失败: HTTP %s %s" % (status, text[:200]))
    return token


def checkin(token: str, device_id: str) -> dict:
    """执行每日签到（claim）。"""
    headers = {
        "Authorization": "Cloud-IDE-JWT " + token,
        "X-User-Region": "cn",
        "x-device-id": device_id,
        "Content-Type": "application/json",
        "User-Agent": "TraeCheckin/1.0",
    }
    status, text = _post("/trae/api/v2/ug/checkin_credits/claim", headers, "{}")
    try:
        return {"http": status, "body": json.loads(text)}
    except json.JSONDecodeError:
        return {"http": status, "body": {"raw": text}}


def main():
    session = os.environ.get("TRAE_SESSION", "").strip()
    if not session:
        print("错误：缺少环境变量 TRAE_SESSION")
        sys.exit(1)

    device_id = os.environ.get("TRAE_DEVICE_ID", "").strip()
    if not device_id:
        device_id = str(random.randint(10**15, 10**16 - 1))
    print("device_id=%s" % device_id)

    try:
        token = get_token(session)
        print("已换取新 JWT，长度=%d" % len(token))
        result = checkin(token, device_id)
        body = result["body"]
        print("签到结果: HTTP %s" % result["http"])
        print(json.dumps(body, ensure_ascii=False))

        # 判定失败：HTTP 非 200 或 code 非 0 且非「已签到」
        code = body.get("code", -1)
        checked = body.get("checked_in", False)
        if result["http"] != 200:
            sys.exit(1)
        if code != 0 and not checked:
            sys.exit(1)
        print("签到完成")
    except Exception as e:
        print("签到异常: %s" % e)
        sys.exit(1)


if __name__ == "__main__":
    main()
