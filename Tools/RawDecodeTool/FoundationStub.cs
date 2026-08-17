// 仅为在 net10.0 harness 中编译 iOS 源码提供 Foundation.PreserveAttribute 桩。
// iOS 项目里该特性由 Microsoft.iOS（Xamarin）绑定提供，真实语义不受影响。
namespace Foundation
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    public sealed class PreserveAttribute : System.Attribute
    {
        public bool AllMembers { get; set; }
        public bool Conditional { get; set; }
    }
}
