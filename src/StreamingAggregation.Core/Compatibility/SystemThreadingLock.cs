// System.Threading.Lock —— net48 兼容垫片。
//
// System.Threading.Lock 是 .NET 9 新增的 BCL 类型（用 Monitor 封装、支持
// EnterScope/IsHeldByCurrentThread）。net48 的 mscorlib 没有它，而 PolySharp
// 只补语言级类型与特性，不会补这种"带实现的运行时类型"，所以必须手写。
//
// 本文件被 Core / Infrastructure / Tests 三个项目以 <Compile Include=... Link=... />
// 链接进各自程序集，声明为 internal：
//   - 三个项目各自一份，符合 PolySharp 的 internal 生成语义，互不依赖；
//   - 不进 public API。App 阶段 2 迁到 net48 后如果也要用 Lock，届时再决定
//     是否把这里提升为 public —— 现在提升会让 App(net10) 同时看到
//     "BCL 的 System.Threading.Lock" 和 "Core 导出的 System.Threading.Lock"，
//     触发 CS0433 类型二义性。
//
// 语义等价性：
//   Lock / EnterScope / Scope 与 .NET 9 的实现保持一致——都是 Monitor 的薄封装，
//   互斥语义、异常行为（未加锁时 Exit 抛 SynchronizationLockException）完全相同。
//   Lock 是密封类且不重写 Equals/GetHashCode，引用相等，与官方一致。

namespace System.Threading;

using System.Diagnostics;

/// <summary>互斥锁对象的轻量包装，<c>lock</c> 语句可用的专用类型。</summary>
internal sealed class Lock
{
    private readonly object _lockObj = new();

    /// <summary>获取进入临界区的作用域，退出作用域即释放锁。</summary>
    public Scope EnterScope() => new(this);

    /// <summary>进入临界区。</summary>
    public void Enter() => Monitor.Enter(_lockObj);

    /// <summary>退出临界区。</summary>
    public void Exit() => Monitor.Exit(_lockObj);

    /// <summary>当前线程是否持有此锁。</summary>
    public bool IsHeldByCurrentThread => Monitor.IsEntered(_lockObj);

    /// <summary>
    /// 锁作用域。用法为 <c>lock (someLock) { ... }</c>，
    /// 编译器会把它展开成 <c>EnterScope()</c> + <c>Dispose()</c>。
    /// </summary>
    /// <remarks>
    /// 必须声明为 <c>public</c>：C# 对 <c>public</c> 成员返回 <c>internal</c> 类型
    /// 会报 CS0050（"不一致的可访问性"），而 <c>lock</c> 语句正是从
    /// <c>EnterScope()</c> 的返回值上取 <c>Dispose()</c>，少一点可见性都编译不过。
    /// 外层 <c>Lock</c> 本身是 internal，所以这里的 public 不扩大程序集边界。
    /// </remarks>
    public ref struct Scope
    {
        private Lock? _lockObj;

        public Scope(Lock lockObj)
        {
            _lockObj = lockObj;
            lockObj.Enter();
        }

        public void Dispose()
        {
            Lock? lockObj = _lockObj;
            if (lockObj is null)
            {
                return;
            }

            // 先清空再退出：若 Exit() 抛异常，finally 里的重复 Dispose 不会二次 Exit。
            _lockObj = null;
            lockObj.Exit();
        }
    }
}
