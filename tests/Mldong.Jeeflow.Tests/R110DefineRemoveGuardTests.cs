using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>audit8 R110：任何实例存在即拒；整批先校验，不半删。</summary>
public class R110DefineRemoveGuardTests
{
    private static (JeeflowFacade Facade, MemoryRepository Repo) New()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        return (new JeeflowFacade(ctx), repo);
    }

    [Fact]
    public async Task DefineRemove_AnyInstance_Rejects_And_BatchDoesNotPartialDelete()
    {
        var (facade, repo) = New();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "r110-guard", TestInfra.LoadFlow("01-simple"));
        var start = await facade.FlowAsync("processInstance/startAndExecute",
            new FlowData { ["processDefineId"] = did, ["operator"] = "applicant" });
        Assert.Equal(0L, Convert.ToInt64(start["code"]));

        const string msg = "存在正在未完成的流程实例，不允许删除！";
        var r = await facade.FlowAsync("processDefine/remove", new FlowData { ["id"] = did });
        Assert.Equal(99999999L, Convert.ToInt64(r["code"]));
        Assert.Equal(msg, r["msg"]?.ToString());
        Assert.NotNull(await repo.FindDefineByIdAsync(did));

        var clean = await TestInfra.SaveFlowDefineAsync(repo, "r110-clean", TestInfra.LoadFlow("02-multi-task"));
        r = await facade.FlowAsync("processDefine/remove",
            new FlowData { ["ids"] = new List<long> { clean, did } });
        Assert.Equal(99999999L, Convert.ToInt64(r["code"]));
        Assert.Equal(msg, r["msg"]?.ToString());
        Assert.NotNull(await repo.FindDefineByIdAsync(clean));
        Assert.NotNull(await repo.FindDefineByIdAsync(did));

        var ok = await facade.FlowAsync("processDefine/remove", new FlowData { ["id"] = clean });
        Assert.Equal(0L, Convert.ToInt64(ok["code"]));
        Assert.Null(await repo.FindDefineByIdAsync(clean));
    }
}
