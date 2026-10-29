# P2-T1 协议样例与固定哈希向量

这些是[协议 v1](../contract.md)的数据夹具，不是已运行的引擎输出。所有环境使用虚构的 runtimeVersion `0.0.0`，以及三组虚拟程序集哈希计算的 buildHash；真实宿主必须返回 EnvironmentMismatch。未来协议单元测试可注入相同测试环境；进程验收必须由真实构建重新录制材料，不允许生产 CLI 跳过环境匹配。

| 文件 | 用途 |
| --- | --- |
| [scenario.json](scenario.json) | 与第一阶段 demo.json 相同的初始场景 |
| [commands.json](commands.json) | 15 个操作：空推进、B 非法换方、六步演示；移动和攻击先一起入队再逐条执行 |
| [empty-commands.json](empty-commands.json) | 初始快照生成/恢复检查所用空操作输入 |
| [initial.snapshot.json](initial.snapshot.json) | 初始空队列，两个计数均为 0，随机 state=42 |
| [finished.snapshot.json](finished.snapshot.json) | 最终空队列，两个计数均为 7（含一次拒绝），回合 3、A 胜 |
| [replay.json](replay.json) | 自包含完整回放，1 个初始检查点和 15 个操作检查点 |
| [run-result.json](run-result.json) | run 完成响应：6 次 Accepted、1 次 Rejected，未请求发布产物 |
| [input-error.json](input-error.json) | 缺 seed 的参数错误，尚无会话或检查点 |
| [event-divergence.replay.json](event-divergence.replay.json) | 仅把 operationIndex=5 的预期移动事件 to.x 改为 0；状态及 hash 保持有效 |
| [divergence-result.json](divergence-result.json) | 上述事件分歧的预期响应，6 个点已匹配；stateHash 相同仍能发现事件不匹配 |
| [hash-vectors.json](hash-vectors.json) | 六组固定字节数及 SHA-256 期望；这是测试数据清单，不是 CLI 文件协议 |

哈希原文：[配置](configuration.canonical.json)、[初始状态](initial-state.canonical.json)、[移动后](moved-state.canonical.json)、[胜负结束](finished-state.canonical.json)、[随机极值](random-max.canonical.json)、[构建组合](build-identity.txt)。前五个文件无 BOM、无结尾换行；构建组合恰好三行且最后一行有 LF。不要通过格式化编辑器添加或删除这些字节。

人工推导的主要检查点：

| operationIndex | 结果 | 已提交/已完成/待执行 | 玩法状态 |
| --- | --- | --- | --- |
| 初始 | initial | 0/0/0 | A、AP=2、Turn=1 |
| 0 | empty | 0/0/0 | 不变 |
| 1 | submitted sequence=1 | 1/0/1 | 不变 |
| 2 | Rejected NotYourTurn | 1/1/0 | 不变，无事件 |
| 3 | submitted sequence=2 | 2/1/1 | 不变 |
| 4 | submitted sequence=3 | 3/1/2 | 不变 |
| 5 | Accepted Move | 3/2/1 | A 到 (1,0)，AP=1 |
| 6 | Accepted Attack | 3/3/0 | B.Health=2，AP=0 |
| 8 | Accepted EndTurn | 4/4/0 | B、AP=2、Turn=2 |
| 10 | Accepted Attack | 5/5/0 | A.Health=2，AP=1 |
| 12 | Accepted EndTurn | 6/6/0 | A、AP=2、Turn=3 |
| 14 | Accepted Attack | 7/7/0 | B.Health=0，AP=1，Winner=A |

7、9、11、13 为后四个命令的提交点，玩法状态不变。回放检查点包含每一个操作，不能只保存上表节选。

复核命令（不依赖项目实现）：

```sh
shasum -a 256 docs/contracts/phase-2/examples/*.canonical.json docs/contracts/phase-2/examples/build-identity.txt
```

Python hashlib 与系统 shasum 的结果已交叉比较。该检查证明固定数据的字节与摘要一致，不证明未来 C# 编码器或回放实现正确。普通 JSON 样例可有缩进和结尾 LF；其 stateHash 必须通过提取 DigestState 再规范化计算，不能对整份快照/回放文件直接取哈希。
