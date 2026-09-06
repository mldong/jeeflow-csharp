# 发版执行清单（jeeflow-csharp v1.0.0）

> **就绪 ≠ 发版**（R4）。本清单在用户账号到位并明确指令后逐步执行；publish/tag/push 本次任务内一律未执行。

## 前置账号/凭据（待用户提供）

| # | 凭据 | 用途 |
|---|---|---|
| 1 | GitHub 远端仓 `mldong/jeeflow-csharp`（建空仓）+ 本机 SSH push 权限 | push 源码 + tag 触发 CI |
| 2 | nuget.org 账号（GitHub 登录即可）+ **Trusted Publishing policy** | 发布 4 个包（免 API key，见 §B） |
| 3 | （仅备用）nuget.org API key | 本机手动 `dotnet nuget push -k` 通道（§C） |
| 4 | （可选，公网 demo 上线时）160 宿主 16087→8093 端口 + ACR 镜像通道 | `?lang=csharp` 公网演示 |

## 发版门票（已全绿，发布前复核一次）

- [x] T0：`dotnet test`（memory）153 用例全绿（含负向变异 + CS3 grep 门禁 + CS7 零传递依赖审计）
- [x] T1：160 真库全绿（分页五键 / hydrate 主键 / 事务回滚无半完成实例 / 并发办理恰一次 / ARCHIVE 落库 / SYNC 字段权限不写穿 / 自清理验证）
- [x] T2：`bash demo/smoke_test.sh` 20/20
- [x] 漂移门禁：`scripts/release-checklist.sh --pre`（flows/ 与 java 源逐字一致）
- [x] stats 一致性：15 key 逐字段一致（consistency/csharp.json；驱动 demo/Mldong.Jeeflow.Consistency）
- [x] 45 action manifest：与 java 实查无差集

## A. 源码 push

```bash
git remote add github git@github.com:mldong/jeeflow-csharp.git
git push github master          # 发布流水线 .github/workflows/release.yml 随 master 上去
```

## B. 主通道：Trusted Publishing（GitHub Actions OIDC，免 API key，官方推荐）

1. nuget.org → Account Settings → **Trusted Publishing** → Create，字段：

| 字段 | 值 |
|---|---|
| Policy Name | `jeeflow` |
| Package Owner | `mldong` |
| CI/CD Provider | `GitHub Actions` |
| Repository Owner | `mldong` |
| Repository | `jeeflow-csharp` |
| Workflow File | `release.yml` |
| Environment | 留空 |
| Scopes | 勾 Push → **Push new packages and package versions**（首发建新包必须选这个）；Unlist 不勾 |
| Glob Patterns | `Mldong.Jeeflow*`（一个 pattern 覆盖 4 包） |

2. 触发发布：推 tag 即自动发布（版本号取自 tag）：

```bash
git tag v1.0.0 && git push github v1.0.0
# Actions「release」跑：pack(4 包) → OIDC 换临时 key → 拓扑序 push（Core → Persist / Repository.MySql → Facade）
```

3. 全绿后做 §D 回拉验证；`NuGet/login@v1` 的 `user` 是 nuget.org 用户名（= Package Owner `mldong`）。

## C. 备用通道：本机手动 push（仅当 Actions 不可用）

```bash
# 版本确认（1.0.0 已钉在 Directory.Build.props；4 包 + demo 同版本继承）
grep "<Version>" Directory.Build.props

# 打包
dotnet pack Jeeflow.sln -c Release -o ./artifacts
# 期望：Mldong.Jeeflow.Core / Repository.MySql / Persist / Facade 各 .nupkg

# 发布（拓扑序：Core → Repository.MySql / Persist → Facade），任一失败即停，不得跳过
dotnet nuget push artifacts/Mldong.Jeeflow.Core.1.0.0.nupkg -k <NUGET_API_KEY> -s https://api.nuget.org/v3/index.json
dotnet nuget push artifacts/Mldong.Jeeflow.Repository.MySql.1.0.0.nupkg -k <KEY> -s https://api.nuget.org/v3/index.json
dotnet nuget push artifacts/Mldong.Jeeflow.Persist.1.0.0.nupkg -k <KEY> -s https://api.nuget.org/v3/index.json
dotnet nuget push artifacts/Mldong.Jeeflow.Facade.1.0.0.nupkg -k <KEY> -s https://api.nuget.org/v3/index.json
```

## D. 回拉验证 + 联邦登记复核

```bash
# 1. 回拉验证（新临时目录，外部装配 → PackageReference 可解析可编译）
mkdir /tmp/nuget-verify && cd /tmp/nuget-verify
dotnet new console && dotnet add package Mldong.Jeeflow.Facade --version 1.0.0
# （nuget.org 解析 200 + 程序可编译运行 = 通过；ProjectReference 换 PackageReference 后 T2 冒烟重放）

# 2. 联邦登记复核
#    - jeeflow-integrations/VERSIONS.md「jeeflow-csharp」节：状态改"已发布"+ 实发号/日期
#    - scripts/stats-7lang-type-verify.py：公网 demo 就绪后跑 8 语言 84 项断言
#    - jeeflow-doc Release workflow（checkout jeeflow-csharp 已配）→ sync:langs → build → 部署
```

## 回滚

- NuGet：已发布版本不可删除——缺陷走 1.0.1 patch（`bump-version.sh 1.0.1 --lang csharp`）。
- 未 push 前全程本地可回退（reset 即可，远程零影响）。
