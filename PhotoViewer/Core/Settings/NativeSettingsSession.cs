using System.Threading.Tasks;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Core.Settings;

/// <summary>原生设置共用工作副本会话，关闭时回写一次，不更改 JSON 契约。</summary>
public sealed class NativeSettingsSession : System.IDisposable
{
    private readonly SettingsViewModel _source;
    private bool _applied;
    /// <summary>可编辑的设置工作副本。</summary>
    public SettingsViewModel WorkingSettings { get; }
    /// <summary>从共享设置建立工作副本，编辑阶段的磁盘保存请求由临时服务消费。</summary>
    public NativeSettingsSession(SettingsViewModel source)
    {
        _source = source;
        WorkingSettings = new SettingsViewModel(new TransientSettingsService(), source.CreateSnapshot(), applyRuntimeSettings: false);
    }
    /// <summary>应用当前工作副本，关闭事件重复触发也只回写一次。</summary>
    public void Apply()
    {
        if (_applied) return;
        _source.ApplySnapshot(WorkingSettings.CreateSnapshot());
        _applied = true;
    }
    /// <summary>结束工作副本订阅生命周期，实际回写由 Apply 明确触发。</summary>
    public void Dispose() => WorkingSettings.Dispose();
    /// <summary>工作副本临时保存通道；实际持久化归源设置，非文件存储替代品。</summary>
    private sealed class TransientSettingsService : ISettingsService
    {
        /// <summary>工作副本读取始终成功，没有读取错误。</summary>
        public string? LastLoadError => null;

        /// <summary>工作副本不落盘，没有保存错误。</summary>
        public string? LastSaveError => null;

        /// <summary>副本已由构造快照填充，读取为空模型。</summary>
        public Task<SettingsModel> LoadAsync() => Task.FromResult(new SettingsModel());
        /// <summary>编辑期不落盘。</summary>
        public Task SaveAsync(SettingsModel model) => Task.CompletedTask;
    }
}
