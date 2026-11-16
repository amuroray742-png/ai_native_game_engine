using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    /// <summary>主线程上的窗口、导航与资源所有权服务，不持有权威玩法状态。</summary>
    /// <remarks>
    /// 预期拒绝通过结果错误码返回；加载、回调和清理故障通过异常报告。
    /// 同步生命周期、焦点和模态通知回调内修改 UI 返回 ReentrantCall。
    /// UIOperationException.Committed 表示本次请求是否已提交，提交后故障不可盲目重试。
    /// </remarks>
    public interface IUI
    {
        /// <summary>打开 HUD、Modal 或 Overlay；同类型已打开时拒绝，关闭缓存可复用。</summary>
        /// <remarks>
        /// token 仅控制提交前；成功后取消不会关窗。HUD/Overlay 按成功提交顺序置顶。
        /// 缓存重开产生新展示句柄，旧句柄不能操作新展示。失败结果不能读取 View/Handle。
        /// </remarks>
        UniTask<UIOpenResult<TView>> OpenAsync<TView>(CancellationToken cancellationToken = default) where TView : UIView;
        /// <summary>加载并入场完成后提交新 Page；准备期间保留旧页，失败尝试恢复原导航。</summary>
        /// <remarks>遵循 OpenAsync 的取消/缓存身份规则；导航忙碌或达到栈深上限时明确拒绝，不排队。</remarks>
        UniTask<UIOpenResult<TView>> PushAsync<TView>(CancellationToken cancellationToken = default) where TView : UIView;
        /// <summary>逻辑关闭指定展示；成功表示已缓存或已实际释放，取决于窗口策略。</summary>
        /// <remarks>Page 仅允许关闭非根栈顶。重复进行中的关闭共享结果；提交后清理失败可携带 CleanupHandle。</remarks>
        UniTask<UIResult> CloseAsync(UIHandle handle);
        /// <summary>释放已关闭缓存或重试失败清理，成功等待窗口 scope 排空。</summary>
        /// <remarks>不能释放可见、仍在导航历史或正在打开/关闭的窗口；cleanup-only 句柄仅支持此入口。</remarks>
        UniTask<UIResult> ReleaseAsync(UIHandle handle);
        /// <summary>优先关闭顶层模态，否则返回前页；仅剩根页时返回 NoBackTarget。</summary>
        UniTask<UIResult> BackAsync();
        /// <summary>释放调用时捕获的关闭缓存，不淘汰导航历史；等待期间重开的会话跳过。</summary>
        /// <remarks>单项失败仍继续其余清理，最后汇总错误；失败项可重试。</remarks>
        UniTask<UIResult> TrimAsync();
        /// <summary>停收、取消并排空本轮窗口和操作，成功后创建新代际并恢复接收。</summary>
        /// <remarks>
        /// 只有 await 完成且返回的 UIResult.Succeeded 为 true 后，宿主才能释放或替换旧 Model。
        /// 已接受 Clear 后的清理故障保持停收，可重试；正常返回的拒绝结果不代表清理成功。
        /// 成功使旧句柄失效，保留窗口定义与宿主 EventSystem，不关闭 Core。
        /// </remarks>
        UniTask<UIResult> ClearAsync();
        /// <summary>将稳定 HUD/Modal/Overlay 置于自身角色层内顶端；Page 不允许绕过导航排序。</summary>
        UIResult BringToFront(UIHandle handle);
        /// <summary>查询稳定可见的窗口，不包含进入/退出候选；可见不代表可交互。</summary>
        /// <remarks>透明页后仍绘制的前页可查询；停止或提交保护期间返回 false。</remarks>
        bool TryGet<TView>(out TView view, out UIHandle handle) where TView : UIView;
        /// <summary>按需捕获不可变完整诊断，包含 inactive 后代组件、资源身份及状态计数。</summary>
        /// <remarks>此调用遍历组件并分配快照，不宜逐帧调用；结果不是最终材质、渲染批次或性能预测。</remarks>
        UIDiagnostics GetDiagnostics();
        /// <summary>导航历史中是否有模态；模态不自动暂停游戏或接管玩法输入。</summary>
        bool HasModal { get; }
        /// <summary>模态零/非零状态改变后的同步通知，订阅者不能在回调内修改 UI 生命周期。</summary>
        /// <remarks>Shut 清除订阅，重启后需重新订阅；通知错误会保留提交状态，Clear 失败重试可能重复通知。</remarks>
        event Action<bool> ModalStateChanged;
    }
}
