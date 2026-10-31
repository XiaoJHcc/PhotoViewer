namespace PhotoViewer.Core.Platform;

/// <summary>消息已经按用户可读语言编写、可直接作为操作失败原因展示的异常。</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message)
    {
    }

    public UserFacingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
