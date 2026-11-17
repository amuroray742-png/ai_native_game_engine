#nullable enable
using AiNative.UnityCore.UI;

namespace AiNative.Examples.GridGame.Play
{
    internal sealed class GridHudPresenter : UIPresenter
    {
        private readonly GridHud _view;
        private readonly GridHudModel _model;
        internal GridHudPresenter(GridHud view, GridHudModel model) { _view = view; _model = model; }
        public override void Bind()
        {
            _view.EndTurn.onClick.AddListener(_model.EndTurn);
            _view.Restart.onClick.AddListener(_model.Restart);
        }
        public override void Resume() { _model.Changed += Refresh; Refresh(); }
        public override void Suspend() { _model.Changed -= Refresh; }
        public override void Unbind()
        {
            _model.Changed -= Refresh;
            if (_view == null) { return; }
            _view.EndTurn.onClick.RemoveListener(_model.EndTurn);
            _view.Restart.onClick.RemoveListener(_model.Restart);
        }
        private void Refresh()
        {
            if (_view == null) { return; }
            if (_model.Frame != null)
            { _view.Show(_model.Frame, _model.Selected, _model.Feedback, _model.Settled); }
            else { _view.ShowFailure(_model.Feedback); }
            _view.SetConnected(_model.Connected && _model.Ready);
        }
    }
}
