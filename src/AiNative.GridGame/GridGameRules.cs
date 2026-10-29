using System;
using System.Linq;
using AiNative.Core;

namespace AiNative.GridGame
{
    public sealed class GridGameRules : IGameRules<GridCommand, GridGameView>
    {
        private readonly ScenarioDefinition _configuration;
        private readonly int _width;
        private readonly int _height;
        private readonly int _actionPointsPerTurn;
        private readonly int _attackDamage;
        private readonly GridPosition[] _obstacles;
        private readonly UnitDefinition[] _units;
        private Side _activeSide = Side.A;
        private int _actionPoints;
        private long _turnIndex = 1;
        private Side? _winner;

        private GridGameRules(ScenarioDefinition definition)
        {
            _width = definition.Width;
            _height = definition.Height;
            _actionPointsPerTurn = definition.ActionPointsPerTurn;
            _attackDamage = definition.AttackDamage;
            _actionPoints = _actionPointsPerTurn;
            _obstacles = definition.Obstacles.OrderBy(position => position.Y).ThenBy(position => position.X).ToArray();
            _units = definition.Units.OrderBy(unit => unit.Id).ToArray();
            // 配置保留初始单位，不能与后续替换元素的运行数组共用存储。
            _configuration = new ScenarioDefinition(_width, _height, Array.AsReadOnly(_obstacles),
                Array.AsReadOnly((UnitDefinition[])_units.Clone()), _actionPointsPerTurn, _attackDamage);
        }

        /// <summary>校验所有领域约束后创建对局。异常表示场景配置无效，不产生可运行实例。</summary>
        public static GridGameRules Create(ScenarioDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            ScenarioValidator.Validate(definition);
            return new GridGameRules(definition);
        }

        /// <summary>返回原始配置及当前玩法状态的隔离副本；宿主须在会话调用之间导出。</summary>
        public GridGameState ExportState() => new GridGameState(_configuration, _units,
            _activeSide, _actionPoints, _turnIndex, _winner);

        /// <summary>完整校验后创建新规则实例；非法状态抛 ArgumentException，不修改已有对局。</summary>
        public static GridGameRules Restore(GridGameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            GridGameStateValidator.Validate(state);
            var rules = new GridGameRules(state.Configuration);
            for (int index = 0; index < state.Units.Count; index++)
            {
                rules._units[index] = state.Units[index];
            }
            rules._activeSide = state.ActiveSide;
            rules._actionPoints = state.ActionPoints;
            rules._turnIndex = state.TurnIndex;
            rules._winner = state.Winner;
            return rules;
        }

        /// <summary>返回当前状态的隔离视图；不推进回合或暴露可变集合。</summary>
        public GridGameView GetState() => new GridGameView(_width, _height, _obstacles, _units,
            _activeSide, _actionPoints, _turnIndex, _winner);

        /// <summary>仅由拥有此实例的 Session 调用。业务拒绝无副作用，伤害为固定值，不消耗随机源。</summary>
        public CommandResult Execute(GridCommand command, DeterministicRandom random)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            if (_winner != null)
            {
                return CommandResult.Rejected("GameFinished");
            }
            if (command.Side != _activeSide)
            {
                return CommandResult.Rejected("NotYourTurn");
            }

            switch (command)
            {
                case Move move:
                    return ApplyMove(move);
                case Attack attack:
                    return ApplyAttack(attack);
                case EndTurn _:
                    return ApplyEndTurn();
                default:
                    return CommandResult.Rejected("UnknownCommand");
            }
        }

        private CommandResult ApplyMove(Move command)
        {
            int index = FindUnit(command.UnitId);
            if (index < 0)
            {
                return CommandResult.Rejected("UnknownUnit");
            }
            UnitDefinition unit = _units[index];
            if (unit.Side != _activeSide)
            {
                return CommandResult.Rejected("NotYourUnit");
            }
            GridPosition destination;
            switch (command.Direction)
            {
                case MoveDirection.Up:
                    destination = new GridPosition(unit.Position.X, unit.Position.Y + 1);
                    break;
                case MoveDirection.Down:
                    destination = new GridPosition(unit.Position.X, unit.Position.Y - 1);
                    break;
                case MoveDirection.Left:
                    destination = new GridPosition(unit.Position.X - 1, unit.Position.Y);
                    break;
                case MoveDirection.Right:
                    destination = new GridPosition(unit.Position.X + 1, unit.Position.Y);
                    break;
                default:
                    return CommandResult.Rejected("InvalidDirection");
            }
            if (destination.X < 0 || destination.Y < 0 || destination.X >= _width || destination.Y >= _height)
            {
                return CommandResult.Rejected("OutOfBounds");
            }
            if (_obstacles.Contains(destination))
            {
                return CommandResult.Rejected("Blocked");
            }
            if (_units.Any(other => other.Position.Equals(destination)))
            {
                return CommandResult.Rejected("Occupied");
            }
            if (_actionPoints == 0)
            {
                return CommandResult.Rejected("NoActionPoints");
            }

            var movedUnit = new UnitDefinition(unit.Id, unit.Side, destination, unit.Health);
            CommandResult result = CommandResult.Accepted(new UnitMoved(unit.Id, unit.Position, destination));
            _units[index] = movedUnit;
            _actionPoints--;
            return result;
        }

        private CommandResult ApplyAttack(Attack command)
        {
            int attackerIndex = FindUnit(command.UnitId);
            if (attackerIndex < 0)
            {
                return CommandResult.Rejected("UnknownUnit");
            }
            UnitDefinition attacker = _units[attackerIndex];
            if (attacker.Side != _activeSide)
            {
                return CommandResult.Rejected("NotYourUnit");
            }
            int targetIndex = FindUnit(command.TargetId);
            if (targetIndex < 0)
            {
                return CommandResult.Rejected("UnknownTarget");
            }
            UnitDefinition target = _units[targetIndex];
            if (target.Side == attacker.Side)
            {
                return CommandResult.Rejected("FriendlyTarget");
            }
            if (attacker.Position.DistanceTo(target.Position) != 1)
            {
                return CommandResult.Rejected("NotAdjacent");
            }
            if (_actionPoints == 0)
            {
                return CommandResult.Rejected("NoActionPoints");
            }

            int health = Math.Max(0, target.Health - _attackDamage);
            var damagedUnit = new UnitDefinition(target.Id, target.Side, target.Position, health);
            var damaged = new UnitDamaged(target.Id, target.Health - health, health);
            CommandResult result = health == 0
                ? CommandResult.Accepted(damaged, new GameEnded(attacker.Side))
                : CommandResult.Accepted(damaged);
            _units[targetIndex] = damagedUnit;
            _actionPoints--;
            if (health == 0)
            {
                _winner = attacker.Side;
            }
            return result;
        }

        private CommandResult ApplyEndTurn()
        {
            Side nextSide = _activeSide == Side.A ? Side.B : Side.A;
            long nextTurn = checked(_turnIndex + 1);
            CommandResult result = CommandResult.Accepted(new TurnChanged(nextSide, nextTurn));
            _activeSide = nextSide;
            _turnIndex = nextTurn;
            _actionPoints = _actionPointsPerTurn;
            return result;
        }

        private int FindUnit(int id) => Array.FindIndex(_units, unit => unit.Id == id);
    }
}
