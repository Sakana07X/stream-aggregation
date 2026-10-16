using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StreamingAggregation.Infrastructure.ViewModels;

/// <summary>
/// 极简 MVVM 通知基类。
/// </summary>
/// <remarks>
/// 整个应用只需要"属性值变了要通知界面"这一件事，为此引入 CommunityToolkit.Mvvm
/// 这类完整框架并不划算：包体积、生成器依赖都远超收益。这里手写 20 行就够，
/// 语义与 <c>ObservableObject</c> 一致——值没变就不通知，避免无谓的绑定刷新。
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 给字段赋新值并在值确实发生变化时触发 <see cref="PropertyChanged"/>。
    /// </summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">目标字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名，由编译器自动填入。</param>
    /// <returns>值发生变化时返回 <see langword="true"/>。</returns>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>触发属性变更通知。</summary>
    /// <param name="propertyName">属性名，由编译器自动填入。</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
