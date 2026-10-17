using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.App.Composition;

/// <summary>
/// 极简服务注册表：登记"服务接口 → 构造方式"，并按需解析单例。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 DI 容器</b>：整个应用只有十来个服务，且全部在组合根一处构造。
/// 引入容器带来的间接层换不来任何可测试性收益——真正的收益来自"业务代码只认接口"这件事，
/// 而那已经由构造函数注入保证了。多写一个 200 行的容器反而多一处可能出错的地方。
/// </para>
/// <para>
/// <b>两个用途</b>：一是解析实例，二是暴露 <see cref="Registrations"/> 供启动自检
/// 与测试核对"每个接口都有实现"。
/// </para>
/// <para>
/// <b>线程安全</b>：单例的创建用锁保护，解析并发调用只会拿到同一个实例。
/// </para>
/// <para>
/// <b>net48 迁移</b>：参数校验从 <c>ArgumentNullException.ThrowIfNull</c>（.NET 6 才有）
/// 改为 <see cref="Guard"/>（本仓库 Core 里已有的等价实现）；<c>Lock</c>（.NET 9）
/// 由 Core 共享的那份 <c>Compatibility/SystemThreadingLock.cs</c> 垫片提供，
/// 由 csproj 以链接方式编进本程序集。
/// </para>
/// </remarks>
internal sealed class ServiceRegistry : IDisposable
{
    private readonly List<Registration> _registrations = [];
    private readonly Dictionary<Type, IReadOnlyList<Type>> _registrationsByService;
    private readonly Dictionary<Type, object> _singletons = [];
    private readonly HashSet<Type> _resolving = [];
    private readonly Lock _gate = new();

    /// <summary>创建注册表。</summary>
    public ServiceRegistry()
    {
        _registrationsByService = new Dictionary<Type, IReadOnlyList<Type>>();
    }

    /// <summary>服务接口 → 全部实现类型。同一接口允许登记多个实现（如两个过滤器）。</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<Type>> Registrations => _registrationsByService;

    /// <summary>
    /// 登记一个单例服务，服务接口就是实现类型本身（无需接口的内部协作者）。
    /// </summary>
    /// <typeparam name="TImplementation">实现类型。</typeparam>
    /// <param name="factory">构造工厂；首次解析时调用一次。</param>
    /// <returns>本注册表，便于链式登记。</returns>
    public ServiceRegistry AddSingleton<TImplementation>(Func<ServiceRegistry, TImplementation> factory)
        where TImplementation : class
    {
        Guard.NotNull(factory);
        return Register<TImplementation, TImplementation>(factory, isSingleton: true);
    }

    /// <summary>
    /// 登记一个单例服务。
    /// </summary>
    /// <typeparam name="TService">服务接口。</typeparam>
    /// <typeparam name="TImplementation">实现类型。</typeparam>
    /// <param name="factory">构造工厂；首次解析时调用一次。</param>
    /// <returns>本注册表，便于链式登记。</returns>
    public ServiceRegistry AddSingleton<TService, TImplementation>(Func<ServiceRegistry, TImplementation> factory)
        where TService : class
        where TImplementation : class, TService
    {
        Guard.NotNull(factory);
        return Register<TService, TImplementation>(factory, isSingleton: true);
    }

    /// <summary>
    /// 登记一个易失服务：每次解析都会新建实例。
    /// </summary>
    /// <typeparam name="TService">服务接口。</typeparam>
    /// <typeparam name="TImplementation">实现类型。</typeparam>
    /// <param name="factory">构造工厂；每次解析都调用一次。</param>
    /// <returns>本注册表，便于链式登记。</returns>
    public ServiceRegistry AddTransient<TService, TImplementation>(Func<ServiceRegistry, TImplementation> factory)
        where TService : class
        where TImplementation : class, TService
    {
        Guard.NotNull(factory);
        return Register<TService, TImplementation>(factory, isSingleton: false);
    }

    /// <summary>
    /// 解析服务。
    /// </summary>
    /// <typeparam name="TService">服务接口或实现类型。</typeparam>
    /// <returns>服务实例。</returns>
    /// <exception cref="InvalidOperationException">未登记、登记了多个实现，或依赖成环。</exception>
    public TService Resolve<TService>()
        where TService : class
    {
        var matches = _registrations.Where(item => item.Service == typeof(TService)).ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"服务 {typeof(TService).FullName} 未注册。");
        }

        if (matches.Length > 1)
        {
            var names = string.Join("、", matches.Select(static item => item.Implementation.Name));
            throw new InvalidOperationException(
                $"服务 {typeof(TService).FullName} 注册了多个实现（{names}），请改用 ResolveAll()。");
        }

        return (TService)Create(matches[0]);
    }

    /// <summary>
    /// 解析某个接口下的全部实现，按登记顺序返回。
    /// </summary>
    /// <typeparam name="TService">服务接口。</typeparam>
    /// <returns>全部实现实例。</returns>
    /// <exception cref="InvalidOperationException">未登记任何实现。</exception>
    public IReadOnlyList<object> ResolveAll<TService>()
        where TService : class
    {
        var matches = _registrations.Where(item => item.Service == typeof(TService)).ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"服务 {typeof(TService).FullName} 未注册。");
        }

        return matches.Select(Create).ToArray();
    }

    /// <summary>释放所有已创建的单例。</summary>
    public void Dispose()
    {
        List<object> instances;

        lock (_gate)
        {
            instances = [.. _singletons.Values];
            _singletons.Clear();
        }

        // 单例之间可能互相引用，先全部取出再逐个释放，避免释放中途又被解析出来。
        foreach (var instance in instances)
        {
            if (instance is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"释放单例 {instance.GetType().Name} 失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>登记一条注册信息。</summary>
    /// <typeparam name="TService">服务接口。</typeparam>
    /// <typeparam name="TImplementation">实现类型。</typeparam>
    /// <param name="factory">构造工厂。</param>
    /// <param name="isSingleton">是否单例。</param>
    /// <returns>本注册表。</returns>
    private ServiceRegistry Register<TService, TImplementation>(
        Func<ServiceRegistry, TImplementation> factory,
        bool isSingleton)
        where TService : class
        where TImplementation : class, TService
    {
        lock (_gate)
        {
            _registrations.Add(new Registration(
                typeof(TService),
                typeof(TImplementation),
                services => factory(services),
                isSingleton));

            _registrationsByService[typeof(TService)] =
                [.. _registrations
                    .Where(item => item.Service == typeof(TService))
                    .Select(static item => item.Implementation)];
        }

        return this;
    }

    /// <summary>按注册项创建或复用实例。</summary>
    /// <param name="registration">注册项。</param>
    /// <returns>服务实例。</returns>
    private object Create(Registration registration)
    {
        if (!registration.IsSingleton)
        {
            return registration.Factory(this);
        }

        lock (_gate)
        {
            if (_singletons.TryGetValue(registration.Service, out var cached))
            {
                return cached;
            }

            if (!_resolving.Add(registration.Service))
            {
                throw new InvalidOperationException(
                    $"服务 {registration.Service.FullName} 的依赖成环，请检查组合根的登记。");
            }

            try
            {
                var created = registration.Factory(this);
                _singletons[registration.Service] = created;
                return created;
            }
            finally
            {
                _resolving.Remove(registration.Service);
            }
        }
    }

    /// <summary>一条注册信息。</summary>
    /// <param name="Service">服务接口。</param>
    /// <param name="Implementation">实现类型。</param>
    /// <param name="Factory">构造工厂。</param>
    /// <param name="IsSingleton">是否单例。</param>
    private sealed record Registration(
        Type Service,
        Type Implementation,
        Func<ServiceRegistry, object> Factory,
        bool IsSingleton);
}