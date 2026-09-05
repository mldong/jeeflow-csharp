namespace Mldong.Jeeflow.Core;

/// <summary>工作流错误码（对齐 Java WfErrEnum）。</summary>
public static class WfErr
{
    /// <summary>decision 节点无法确定下一步执行路线</summary>
    public const int NotFoundNextNode = 20010001;
    /// <summary>没有流程定义</summary>
    public const int NotFoundProcessDefine = 20010002;
    /// <summary>没有进行中的流程任务</summary>
    public const int NotFoundDoingProcessTask = 20010003;
    /// <summary>当前参与者不能执行该流程任务</summary>
    public const int NotAllowedExecute = 20010004;
    /// <summary>存在正在未完成的流程实例，不允许删除</summary>
    public const int ExistUnFinishInstance = 20010005;
    /// <summary>必需的 SPI 未注册</summary>
    public const int SpiNotRegistered = 20010006;
}

/// <summary>工作流引擎异常（对齐 Java JeeflowException）。code=-1 时门面统一出口仍为 99999999 信封。</summary>
public class JeeflowException : Exception
{
    public int Code { get; }

    public JeeflowException(string message) : base(message) => Code = -1;

    public JeeflowException(int code, string message) : base(message) => Code = code;

    public JeeflowException(int code, string message, Exception cause) : base(message, cause) => Code = code;

    public JeeflowException(int err) : base(WfErrMessage(err)) => Code = err;

    public static JeeflowException Of(int code, string message) => new(code, message);

    private static string WfErrMessage(int code) => code switch
    {
        WfErr.NotFoundNextNode => "decision节点无法确定下一步执行路线",
        WfErr.NotFoundProcessDefine => "没有流程定义",
        WfErr.NotFoundDoingProcessTask => "没有进行中的流程任务",
        WfErr.NotAllowedExecute => "当前参与者不能执行该流程任务",
        WfErr.ExistUnFinishInstance => "存在正在未完成的流程实例，不允许删除！",
        WfErr.SpiNotRegistered => "必需的 SPI 未注册，请在 ServiceContext 注册实现",
        _ => $"工作流错误({code})",
    };
}
