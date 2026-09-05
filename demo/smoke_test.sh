#!/usr/bin/env bash
# jeeflow-csharp demo 端到端冒烟（T2）：路由/信封/负向/主链路
# 前置：demo 已启动（JEEFLOW_DEMO_STORE=memory dotnet run --project demo/Mldong.Jeeflow.Demo）
# 用法：bash demo/smoke_test.sh [BASE_URL]   默认 http://127.0.0.1:8093
set -u
BASE="${1:-http://127.0.0.1:8093}"
PASS=0; FAIL=0

ok()   { PASS=$((PASS+1)); echo "  ✓ $1"; }
fail() { FAIL=$((FAIL+1)); echo "  ✗ $1"; }
check_code0() { echo "$1" | grep -q '"code":0' && ok "$2" || fail "$2 → $1"; }
check_999()   { echo "$1" | grep -q '"code":99999999' && ok "$2" || fail "$2 → $1"; }

wf() { curl -s -X POST "$BASE/wf/$1" -H 'Content-Type: application/json' -d "${2:-{\}}"; }
jget() { python -c "
import sys, json
d = json.loads(sys.stdin.read)
" 2>/dev/null; }

j() { python -c "
import sys, json
d = json.load(sys.stdin)
path = '$1'.split('.')
v = d
for p in path:
    if p: v = v[int(p)] if p.isdigit() else v[p]
print(v)
"; }

echo "── 路由 ──"
HEALTH=$(curl -s "$BASE/health")
echo "$HEALTH" | grep -q '"engine":"jeeflow-csharp"' && ok "health" || fail "health → $HEALTH"

echo "── 种子契约 ──"
PAGE=$(wf processDefine/page '{"pageNum":1,"pageSize":5}')
check_code0 "$PAGE" "processDefine/page 五键"
echo "$PAGE" | grep -q '"recordCount":15' && ok "15 共享流程种子" || fail "种子数非 15 → $PAGE"
LBT=$(wf processDesign/listByType)
check_code0 "$LBT" "processDesign/listByType"

echo "── 负向 ──"
check_999 "$(wf no/such/action)" "未知 action → 99999999"
check_999 "$(wf processDefine/page '{bad')" "非法 body → 99999999"
check_999 "$(wf processDefine/detail '{"id":99999999}')" "不存在 detail → 99999999"
check_999 "$(wf processInstance/stats/trend '{"start":"2026-08-01 00:00:00","granularity":"hour"}')" "trend 缺 end → 99999999"

echo "── 主链路：发起 → 会签办理(07 比例 2/4) → 软拒绝场景 → 完成 ──"
START=$(wf processInstance/startAndExecute '{"processDefineId":7,"operator":"userA"}')
check_code0 "$START" "发起（07 比例会签）"
IID=$(echo "$START" | j data.processInstanceId)

execute() { wf processTask/execute "{\"processTaskId\":$1,\"operator\":\"$2\",\"submitType\":${3:-1}}"; }
todo_id() {
  local r; r=$(wf processTask/todoList "{\"operator\":\"$1\"}")
  echo "$r" | python -c "
import sys, json
d = json.load(sys.stdin)
rows = d['data']['rows']
print(rows[0]['id'] if rows else '')
"
}

TID=$(todo_id userA)
[ -n "$TID" ] && check_code0 "$(execute "$TID" userA 1)" "userA 办理(1/4)" || fail "userA 无待办"
TID=$(todo_id userB)
[ -n "$TID" ] && check_code0 "$(execute "$TID" userB 1)" "userB 办理(2/4 → 比例达成 merged)" || fail "userB 无待办"

# 比例达成后剩余会签任务应被废弃，实例推进到 end → FINISHED(20)
DETAIL=$(wf processInstance/detail "{\"id\":$IID}")
check_code0 "$DETAIL" "instance/detail"
STATE=$(echo "$DETAIL" | j data.state)
[ "$STATE" = "20" ] && ok "实例已完成(20)" || fail "实例状态=$STATE（期望 20）"

echo "── 抄送/视图 ──"
check_code0 "$(wf processInstance/createCCInstance "{\"processInstanceId\":$IID,\"operator\":\"userA\",\"actorIds\":[\"boss\"]}")" "createCCInstance"
check_code0 "$(wf processInstance/ccList '{"operator":"boss"}')" "ccList"
check_code0 "$(wf processInstance/approvalRecord "{\"id\":$IID}")" "approvalRecord"
check_code0 "$(wf processTask/latest "{\"processInstanceId\":$IID}")" "taskLatest"
STATS=$(curl -s "$BASE/api/stats?operator=userA")
echo "$STATS" | grep -q "todoCount" && ok "api/stats" || fail "api/stats → $STATS"
check_code0 "$(wf processDefine/getLastByName '{"processDefineName":"07-countersign-ratio"}')" "getLastByName"

echo "── reset ──"
check_code0 "$(curl -s -X POST "$BASE/api/reset")" "api/reset"

echo
echo "结果: PASS=$PASS FAIL=$FAIL"
[ "$FAIL" = "0" ]
