namespace AiNative.Core
{
    /// <summary>实例装配后由一个 Session 独占。Execute 在逻辑步中修改状态；GetState 返回隔离视图。</summary>
    public interface IGameRules<in TCommand, out TView>
    {
        CommandResult Execute(TCommand command, DeterministicRandom random);
        TView GetState();
    }
}
