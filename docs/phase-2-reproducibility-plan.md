# 第二阶段：复现与 Agent 工具实施计划

> 历史记录：本文保留当时的设计与验证证据；当前运行入口及契约已迁移到 [v2](framework-v2.md)，本文旧命令不作为当前验收入口。
状态：P2-T1 契约文档及样例已固化（2026-10-29）；P2-T2 已完成进程内状态导出恢复及本地验收，P2-T3 已完成快照文件、摘要与单文件发布的本地验收，P2-T4 已完成批处理 CLI 与跨进程本地验收，P2-T5 已完成回放与首个分歧定位的本地验收，P2-T6 已完成本地验证、远端 CI 和上传制品核验（2026-10-30），第二阶段验收完成。[公共 API 与协议 v1](contracts/phase-2/contract.md)是具体签名、字段及参数依据，其中 Core 与 GridGame 管理 API 已实现，快照文件内部 API 已实现，run/inspect/replay CLI 已实现。

设计依据见[设计文档](ai-native-game-engine-design.md)，实现约束见[架构与代码规范](engineering-guidelines.md)，现有能力见[第一阶段验收记录](phase-1-headless-gameplay-plan.md)。T1 仅编写契约与样例；T2 在现有项目内实现进程内管理 API，T3 在 Headless 内实现快照边界，T4 实现命令文件与 CLI，T5 实现回放记录和验证；均不新增依赖或项目。

第三阶段前已发生[宿主边界调整](host-boundary-and-presentation.md)：纯协议实现移入共享库，宿主使用完整对局入口，增加不执行模拟的证据比较。下文任务和验收记录保留第二阶段当时事实；当前新增 API 与表现契约以该文档为准。

## 1. 目标、基线与范围

目标：Agent 能提交外部命令文件、取得结构化执行结果，在进程退出后恢复对局，并通过回放定位首个分歧。

第一阶段已有 FIFO 单步执行、隔离查询、确定性随机源、严格场景加载和固定六步演示；既有 59 项测试是第一阶段证据，不作为本阶段通过依据。P2-T2 已补齐进程内完整状态导出及随机状态恢复；P2-T3 已补齐快照文件与生产状态摘要；P2-T5 已实现回放执行与分歧报告。

本阶段包含：

- 完整状态导出与恢复、空队列快照、配置内容标识及兼容性检查。
- 规范化状态摘要、结构化差异、命令批处理、回放记录与验证。
- CLI 的机器输出、稳定错误分类、跨进程验证及现有 CI 检查入口扩展。

保持现有移动、固定伤害、回合与胜负规则。Unity、常驻会话、MCP、网络服务、存档迁移、运行中热更新、性能报告和通用规则框架不在本阶段范围内。不增加随机伤害来测试随机恢复；使用测试规则消费随机数。

## 2. 模块职责与新增边界

| 模块 | 本阶段责任 | 边界 |
| --- | --- | --- |
| Core | 会话计数、随机状态导出恢复、可保存边界及故障隔离 | 不认识网格字段，不读写文件，不引用 JSON 协议 |
| GridGame | 完整玩法状态导出、恢复校验与实例构造 | 区分场景创建和对局恢复，不解析文件或决定进程退出码 |
| Headless | 配置封装、JSON 编解码、摘要和差异、批处理与回放、文件发布及进程输出 | 通过公开管理入口装配与恢复，不直接修改规则字段 |
| Tests | 状态、版本、随机连续性、协议与进程测试 | 用固定期望和独立断言验证，不仅比较同一实现的两次输出 |

完整状态入口用于解决当前视图无法恢复对局的问题。会话恢复入口用于接续随机状态与调度位置；二者均是管理操作，不混入玩法命令。宿主负责组合两部分，在候选实例全部验证成功后交付调用方。

保留现有查询视图供观察使用，不将其直接当作存档。完整状态对象必须隔离集合及元素，不能形成新的外部写入通道。优先在现有三个生产项目内实现；P2-T1 已明确最小公共签名，不预先建设通用序列化接口或插件注册系统。文件格式采用宿主已有的 System.Text.Json；新增格式的必要性、版本和兼容影响由本文约束。

## 3. 调度与检查点语义

| 概念 | 定义 |
| --- | --- |
| `sequence` | 每次成功 Submit 分配的正整数序号；新会话从 1 开始，拒绝入队的输入不占序号 |
| `lastSubmittedSequence` | 最后成功入队的序号；新会话为 0 |
| `completedStepCount` | 已正常返回结果的非空 Advance 次数；新会话为 0，业务拒绝也计数 |
| `turnIndex` | 玩法回合，从 1 开始，仅成功 EndTurn 增加 |
| 空推进 | 返回无命令结果，不调用规则，不改变计数或随机状态 |
| 故障推进 | 没有正常完成检查点；会话进入故障态，不再查询、推进或保存 |

本阶段无撤销、丢弃队列或重排：健康实例满足 `lastSubmittedSequence = completedStepCount + pendingCount`。已完成命令的 sequence 等于其完成后的 completedStepCount。保留两个名称以区分“提交”和“完成”，不混用游戏回合。

保存仅允许健康、未处于执行中、`pendingCount = 0` 的实例；新建但尚未执行的实例也是合法边界。保存失败不得自动清空或推进队列。恢复后队列为空，计数延续；下一次成功提交返回保存序号加 1。计数溢出必须明确失败，不允许回绕。

业务拒绝可以改变队列和调度计数，但不改变玩法状态、随机状态或产生事件。因此“拒绝前后状态摘要相同”指第 5 节的玩法摘要，调度位置另行比较。

## 4. 快照契约

### 4.1 文件内容与版本

首版使用自包含 JSON 快照，包含以下必填部分；允许 null 的字段仅为明确可空的 winner。

| 部分 | 必须保存的内容 |
| --- | --- |
| 文件身份 | `snapshotFormatVersion = 1`、`rulesId = grid-game`、`rulesVersion = 1` |
| 配置身份 | `configFormatVersion = 1`、`configHash`、完整原始场景的领域内容 |
| 会话 | lastSubmittedSequence、completedStepCount；pendingCount 固定为 0 |
| 随机源 | `algorithm = splitmix64-v1`、当前内部 state |
| 玩法运行状态 | 单位 ID、阵营、当前位置和当前生命值；activeSide、actionPoints、turnIndex、winner |
| 校验 | `digestVersion = 1`、当前 stateHash |
| 来源环境 | runtime 版本、操作系统、进程架构、构建内容标识 |

配置领域内容保留地图大小、障碍、单位初始 ID/阵营/位置/生命值、attackDamage、actionPointsPerTurn 和既有场景 formatVersion。地图及规则参数从内嵌配置恢复，不再在运行状态中存第二份。configFormatVersion 是新封装版本，既有场景文件格式仍为 1，不要求改写 demo.json。

configHash 是规范化配置内容的 SHA-256，不是文件路径、修改时间或用户任填的标签；规范化细则见第 5 节。初始 seed 只作为创建输入记录在运行请求中；恢复必须使用当前随机内部 state，不能重新播种或重放若干次来猜测位置。

为避免 JSON 消费者丢失整数精度，所有 Int64/UInt64 字段（包括 sequence、步数、turnIndex、seed、随机 state）采用无前导零的十进制字符串，零写作 `"0"`；拒绝符号、空白、指数形式和越界值。Int32 字段使用 JSON 整数。新协议枚举按文档名称区分大小写，拒绝整数和未知值；既有场景加载器接受 A/B 大小写的行为保持不变，规范化后统一大写。

所有新格式拒绝重复属性、未知字段、缺失字段、类型不符和非法 null；不通过忽略字段静默兼容。格式、规则、随机算法或摘要版本不支持时明确拒绝，不自动迁移。自包含配置通过结构、领域和内容哈希校验即可使用；若调用方另提供预期配置，其 configHash 必须匹配。

环境记录不参与玩法摘要。首版恢复和回放验证只支持与当前进程相同的 runtime 版本、操作系统、架构及构建内容标识，否则返回 EnvironmentMismatch；不把不受支持的验证报告为玩法分歧。构建标识使用实际加载程序集的内容哈希组合；第三阶段前扩充共享库和 JSON 依赖清单，当前编码顺序见 P2-T1 契约；Git 提交号可补充诊断，但不能代替未提交改动的构建身份。后续放宽此策略需单独取得兼容证据。

### 4.2 恢复校验与替换

恢复按以下顺序执行，任何失败均不替换当前实例：

1. 宿主读取并严格解析文件，检查版本和环境，校验配置及 configHash。
2. Core 校验计数、空队列条件及随机算法/状态范围；GridGame 校验运行状态。
3. 用隔离的数据创建候选规则与会话，导出候选状态并重新计算 stateHash。
4. 哈希匹配后宿主才替换会话引用；失败释放候选对象并返回结构化错误，旧会话保持原状。

初始场景校验不能直接替代运行状态校验。运行状态至少满足：

- 双方各一个单位；ID 和阵营与配置一致，集合无缺失、重复或额外单位。
- 位置在地图内，不重叠、不占障碍；生命值在 0 到该单位初始生命值之间。
- actionPoints 在 0 到 actionPointsPerTurn 之间，turnIndex 至少为 1；奇数回合 A 行动，偶数回合 B 行动。
- winner 为空时双方均存活；winner 非空时胜方存活、败方生命值为 0，且 activeSide 为胜方；拒绝双方死亡或胜方与状态矛盾。
- 健康空队列快照两种调度计数相等且非负；turnIndex 不大于 completedStepCount + 1，比较时避免溢出。

上述检查保证结构与规则不变量，不宣称证明任意外部状态在历史上可达。哈希用于内容一致性检查，不是签名或防篡改认证。

### 4.3 保存与文件失败

导出、编码和磁盘写入不修改模拟状态。宿主先写目标目录中的临时文件，完整写入并关闭后发布到目标路径；失败保留原有效文件并报告 HostFailure。该约定保证受支持文件系统上的单文件发布，不承诺断电耐久性或多个输出文件的事务。

首版输出目标必须不存在，存在时拒绝覆盖；不提供隐式覆盖。多个证据文件分开发布，失败结果注明已发布路径，不报告整次成功；临时文件尽力清理。输入读取失败归 InputError，输出发布失败归 HostFailure。

## 5. 状态摘要与结构化差异

摘要使用 SHA-256、UTF-8 无 BOM 的紧凑 JSON，十六进制输出统一小写。规范化显式规定字段顺序与数组顺序，不依赖反射成员顺序、运行时默认序列化或无序容器遍历。

- 配置字段顺序：formatVersion、width、height、obstacles、units、actionPointsPerTurn、attackDamage。障碍按 Y、X 升序；单位按 ID 升序。位置字段为 x、y；配置单位字段为 id、side、position、health。
- 摘要根字段顺序：digestVersion、rulesId、rulesVersion、configHash、random、game。random 字段为 algorithm、state；game 字段为 units、activeSide、actionPoints、turnIndex、winner；运行单位字段与配置单位相同，使用当前值。
- 所有字符串使用已约定的固定标识、枚举、十进制或哈希字符；无缩进、无尾随换行，null 显式输出。输入 JSON 的空白、属性顺序和语义无关的数组排列不影响规范化结果。

stateHash 覆盖玩法权威状态、随机内部状态及影响后续行为的规则/配置身份；不含调度计数、待执行队列、事件、环境、日志、路径和性能信息。摘要相同不等于会话相同；回放检查点必须另外比较计数、队列数量及结果事件。P2-T1 已提供固定规范化字节与预期哈希样例，不能仅用被测编码器生成测试期望。

分歧报告含检查点位置、命令序号、输入命令、expectedHash/actualHash，以及有稳定路径的 expected/actual 字段差异，例如 `game.units[id=2].health`。数组按实体 ID 定位，缺失/新增分别报告；事件按产生顺序比较类型与载荷。首版保留完整预期规范化状态以支持差异，不能只保存哈希再要求用户猜测原因。

## 6. 命令批处理与回放契约

### 6.1 输入及推进位置

命令文件包含 `commandFormatVersion = 1` 和有序 operations。仅有两类操作：

- `submit`：包含一个 Move、Attack 或 EndTurn 的带类型命令；沿用既有 side、unitId、direction、targetId 语义，不接受目标坐标移动。
- `advance`：一次推进，处理一条待执行命令，或记录空推进。

整个文件先完成结构校验，再创建或恢复实例；未知类型、缺失参数、非法枚举属于输入错误，不进入规则。合法结构中的不存在单位、越界或不合法回合属于业务拒绝，在推进时判定。命令结构校验不提前裁定玩法。

operations 数组下标从 0 开始，唯一确定提交与推进顺序。不自动推进剩余队列；文件结束队列非空返回 PendingCommands 输入失败，报告已完成执行，不保存最终快照或发布成功回放。预检查可发现的不平衡队列应在执行前拒绝，仍保留运行时边界检查。

### 6.2 回放文件

`replayFormatVersion = 1` 的自包含文件包含初始快照、完整 operations、初始检查点和每个 operation 完成后的预期检查点。初始快照可以来自新对局或合法中途快照；加载/恢复只发生在起点，不作为普通命令插入记录。

每个检查点包含：operationIndex（初始点单独标记）、lastSubmittedSequence、completedStepCount、pendingCount、stateHash、完整规范化状态，以及本操作的结果：

- submit：接收的 sequence。
- 非空 advance：sequence、业务成功/拒绝及稳定错误码、有序事件类型与载荷。
- 空 advance：明确的 empty 结果，无命令序号或事件，不伪装为 Accepted。

记录器保存实际执行结果；验证器必须执行初始快照和 operations，逐检查点与既存期望比较，不能现场重建期望覆盖基线。初始快照和检查点的内容/哈希不一致属于 InvalidReplay；实际运行与有效期望不同才属于 ReplayDivergence。检查点缺失、重复、多余、计数矛盾或索引不连续均拒绝。

业务拒绝也完整记录，回放时必须再次得到相同拒绝、无事件和未变的玩法摘要。首个分歧出现即停止，报告初始点或 operationIndex、sequence（若存在）、差异类别与字段差异。只修改操作输入且保持原期望有效时，应定位对应首个分歧；不能只比较最终胜负。

模拟故障导致记录未完成，输出失败材料及最后成功检查点，不发布完整成功回放。历史版本迁移、分支编辑器和压缩回放不在本阶段实现。

## 7. CLI 能力与失败语义

首版继续使用 Headless 可执行程序，新增显式批处理子命令；具体参数拼写已在 P2-T1 固定，后续实现帮助并遵循样例。无参数演示及既有单场景路径调用保持原行为。下表能力已随 P2-T4/T5 实现，具体本地验收边界见本文件末尾记录。

| 能力 | 输入 | 输出 |
| --- | --- | --- |
| run | 场景与显式 seed，或快照（二选一）；命令文件；可选输出路径 | 初始/逐步/最终状态、结果与事件；可选最终快照和回放 |
| inspect | 合法快照 | 已校验状态、摘要及版本信息；不执行命令 |
| replay | 完整回放文件 | 已比较检查点数量，成功或首个分歧 |

空 operations 允许创建初始快照或验证恢复状态。恢复输入禁止另传 seed；配置来自快照，不能悄悄用当前 demo.json 替换。命令文件内不嵌文件路径；显式 CLI 相对路径按当前工作目录解析。

新子命令 stdout 输出一个有 `protocolVersion = 1` 的 JSON 结果；stderr 输出诊断。结果至少包含 status、category、稳定 code、message、可定位的 operationIndex/sequence、已完成结果及已发布证据路径。不得要求 Agent 解析 message 或 stderr。错误发生在 stdout 不可用时尽力写 stderr 并返回失败，不能保证仍有完整 JSON。

| 退出码 | 分类 | 语义 |
| --- | --- | --- |
| 0 | Completed | 批处理完成，或回放全部匹配；业务拒绝仍在逐步结果中明确标出 |
| 1 | SimulationFailure / HostFailure | 规则非预期故障，或输出/文件发布等宿主故障；用 category 区分 |
| 2 | InputError | 参数、文件读取、结构、版本、环境、快照不变量或记录完整性校验失败 |
| 3 | ReplayDivergence | 有效回放首次出现预期与实际不一致 |

run 默认继续执行业务拒绝后的后续 operations；退出 0 只表示批处理完成，不表示所有玩法命令成功。顶层同时给出 accepted/rejected 数量。replay 中预期的拒绝匹配可正常通过。既有固定演示仍沿用原有最终断言与退出码，不复用批处理“完成”的含义。

## 8. 任务拆分、依赖与交付

P2-T1 已交付契约文档与数据样例；P2-T2–T5 已实现，P2-T6 已完成本地与远端 CI 验收，上传证据已下载核验。实现者负责实现与初步验证，独立 Reviewer 负责需求、边界和证据审查；责任分配见 P2-T1 契约第 7 节，写代码者不能承担该代码最终独立审查。默认顺序集成，不预设并行收益。

| 任务 | 依赖 | 影响模块及交付 | 验收入口 |
| --- | --- | --- | --- |
| P2-T1 契约固化（已完成） | 本计划 | 明确公共管理 API、CLI 参数、严格字段表、协议样例、错误码和固定哈希向量；审核版本及环境策略 | 逐项对照第 3–7 节，无未决共享契约；确认最小接口必要性 |
| P2-T2 完整状态与恢复（已完成） | T1 | Core 的计数/随机恢复、GridGame 的状态导出与恢复校验；保留现有查询接口 | V1–V4、V8；异常后仍遵守故障态规则 |
| P2-T3 快照文件与摘要（已完成） | T2 | Headless 严格编码/解码、配置身份、规范化摘要、环境检查、单文件发布 | V3–V6、V11；固定字节与哈希向量独立断言 |
| P2-T4 通用批处理 CLI（已完成） | T3 | 外部命令 operations、run/inspect、机器输出、兼容原演示 | V7–V9、V11；从另一个工作目录调用真实进程 |
| P2-T5 回放与首个差异（已完成） | T4 | 记录器、验证器、检查点、字段差异及 replay 入口 | V5、V6、V9、V10；有效拒绝和空推进均能回放 |
| P2-T6 集成与交付证据（已完成） | T2–T5 | 固定案例、进程连续性、独立审查、CI 入口、README 与实际验收记录 | V1–V12 全部取得证据，审查后重跑受影响检查 |

各任务只实现本表及所引用契约，不附带玩法扩展或无关重构。沿用设计文档的三轮“假设、修改、验证”预算，保留失败材料。改变状态所有权、兼容策略、退出语义或验收目标须先同步文档并由人决定；常规实现选择由实现者处理。公共类型名和参数拼写已在 T1 固化，不将此项留给多个模块各自决定。

## 9. 验收矩阵

| 编号 | 固定场景或操作 | 必须观察到的结果 |
| --- | --- | --- |
| V1 | 默认六步演示，分别在 0–6 步的空队列边界保存恢复 | 恢复点及后续每步状态、随机状态、序号、结果和事件与连续执行一致；最终 A 胜、Turn=3、A.Health=2、B.Health=0、AP=1 |
| V2 | 使用消费随机数的测试规则，在已消费至少三次后恢复；覆盖 0 和 ulong.MaxValue 初始种子 | 后续随机输出与连续执行一致；使用固定向量辅助断言，恢复不回到 seed；会话相互隔离 |
| V3 | 未推进的新局、耗尽 AP、已结束对局、含待执行命令及故障实例 | 前三者允许保存恢复；后两者明确拒绝保存且不自动推进、清队列或生成成功产物 |
| V4 | 修改单位 ID/阵营、重复位置、生命值范围、行动点、胜方、回合奇偶或调度计数 | 恢复拒绝；已有健康实例状态、随机数和下一提交序号不变 |
| V5 | 改变格式、规则、随机、摘要版本或环境；修改配置内容但保留旧 hash；额外提供不同配置 | 返回准确兼容性或内容错误，不替换状态；不报告成回放玩法分歧 |
| V6 | 改 JSON 空白、属性顺序、障碍/单位排列；分别改变一个真实状态字段、随机 state 或规则参数 | 前者摘要不变；后者在重新生成合法材料时摘要改变；原 hash 未更新时拒绝无效材料 |
| V7 | 先连续提交再逐条推进；穿插空推进、合法命令和 NotYourTurn/Blocked/NoActionPoints 拒绝 | 序号与完成计数符合第 3 节；拒绝无事件且玩法/随机摘要不变；空推进不计步 |
| V8 | 多次查询/导出并尝试修改返回集合；恢复后修改原输入对象 | 内部状态与摘要不变；历史视图不随后续执行变化 |
| V9 | 同配置和输入在同一支持环境中重复运行 10 次，并在新进程恢复第 3 步后执行剩余命令 | 各检查点匹配；连续与分段执行得到相同最终结果；原场景文件不可用仍可用自包含快照恢复 |
| V10 | 在有效回放中修改一条命令但保留原期望；另造预期事件变化、缺失检查点和无效 hash | 命令/事件偏差报告首个位置与 expected/actual；缺失或损坏记录归输入错误；不只返回布尔值 |
| V11 | malformed JSON、重复/未知字段、64 位极值与溢出、丢失输入文件、输出不可写或目标已存在 | 输入与宿主故障分类正确；stdout 可解析或输出故障明确退出；不破坏已有文件；报告部分已发布产物 |
| V12 | 运行原演示、原测试及新测试，在现有 CI 工作流执行相关检查 | 原行为保持，新检查可重复执行；区分本地与远端结果，未运行的 CI 不写为通过 |

V1 的保存边界使用“提交一条、推进一条”的演示方式；V7 单独验证队列不为空时的推进与检查点，不能用逐条提交测试替代队列测试。V10 要区分合法预期发生偏差与文件自身损坏，避免把所有异常归成同一个错误。

## 10. 阶段退出与证据记录

阶段退出要求：P2-T1–T6 完成，V1–V12 有可重复证据，独立审查无未解决阻断问题，审查后的最终代码通过相关构建、测试、格式及 CLI 进程检查。不以测试数量增长代替验收矩阵，不以本机通过承诺跨平台一致性。

实施后记录：提交/构建身份、SDK 与 runtime、OS/架构、执行命令、通过/失败结果、固定输入与输出位置、首个分歧示例、Reviewer 与问题处理结论。协议样例及小型固定案例随实现入库，临时产物不直接堆入仓库；远端 CI 证据仅在实际运行后补充。

本阶段已完成 T1 契约、T2 进程内实现、T3 快照文件边界、T4 批处理 CLI 及 T5 回放验证；P2-T6 的本地行为验收、证据索引、远端 CI 与制品核验已完成，第二阶段验收关闭；PR 合并不在本次验收操作之内。继续遵循已固化的 P2-T1 契约；任何后续实际验收另记日期和证据，不改写第一阶段历史记录。

### P2-T1 契约交付记录（2026-10-29）

已提供[公共 API、CLI 和协议字段表](contracts/phase-2/contract.md)、[完整数据样例及固定哈希向量](contracts/phase-2/examples/README.md)。确定了空队列会话导出/恢复、玩法完整状态、参数互斥、JSON 联合分支、错误码和构建身份编码。示例环境为虚构夹具，不能视为真实构建的兼容证据。

本次仅检查文档、样例内容及哈希，不编写实现代码，不运行引擎构建或测试。P2-T2–T6 和 V1–V12 行为验收仍未完成。

实际文档验证：16 份 JSON 严格解析通过；6 组向量的字节数与 SHA-256 经 Python hashlib 和系统 shasum 双重核对；完整回放 16 个检查点（含初始点）的摘要与调度计数一致；6 份 Markdown 的 42 个本地链接、代码围栏与空白格式检查通过，git diff --check 通过。规范化字节文件按契约故意不含结尾换行。

独立 Reviewer Agent `contract_review` 已只读审查公共 API、CLI、字段表和样例，独立手列操作状态并复核哈希、事件及最终快照，未发现阻断问题。其关于初始状态比较应忽略合法数组重排的建议已纳入契约。此次审查不替代后续代码独立审查；当前没有未解决的 T1 阻断事项。

### P2-T2 进程内状态导出与恢复（2026-10-29）

本轮仅修改 Core、GridGame 及对应测试，Headless 和文件协议未实现新功能。新增 SessionState、GridGameState 及内部玩法状态校验器：分别承载空队列会话恢复数据、隔离的初始配置与当前玩法状态、运行状态不变量校验。没有新增生产依赖或项目。

- 会话计数区分提交、完成、业务拒绝与空推进；恢复接续序号和当前 SplitMix64 状态，不调用规则或消费随机数。序号耗尽在入队前明确失败。
- Execute/GetState 期间统一拒绝 Submit、Advance、GetState 和 ExportState 重入；传播的规则故障终止会话，非空队列导出失败不清队列。
- 玩法恢复独立校验配置、单位身份/生命/位置、AP、回合与胜方，允许合法的零 AP 和已结束对局；导出保留原始配置，恢复不重置当前回合。
- 新增 SessionStateTests 和 GridGameStateTests，覆盖演示 0–6 步全部进程内恢复边界及后续逐步结果/事件、两种种子的随机连续性、实例隔离、25 类非法玩法状态、计数/算法/null/重入/溢出与故障边界。

验收边界：本轮完成 V1–V4、V8 所涉及的进程内 API 检查，不能将其视为这些矩阵项的文件、摘要或跨进程验收已通过。`turnIndex - 1 <= completedStepCount` 是 Headless 组合恢复时的跨模块约束，按 T1 留待后续装配实现，未放入通用 Core 或单独玩法恢复中。仍不支持快照文件、内容 hash、版本/环境协议、CLI 或回放执行。

本地验证环境：macOS 26.6 / arm64，SDK 10.0.202、runtime 10.0.6；基于 `f4bf6ad` 的未提交工作区修改，Debug 构建。最终审查后实际执行：

- `dotnet build --no-restore`：成功，0 警告、0 错误。
- `dotnet test --no-build --no-restore`：117 项通过，0 失败、0 跳过（较第一阶段新增 58 项）。
- `dotnet format --verify-no-changes --no-restore`：通过。首次检查发现新测试的 switch 分支换行问题，仅对该文件修复格式后通过。
- `dotnet run --project src/AiNative.Headless --no-build`：默认六步演示通过，最终 Winner=A、Turn=3、A.Health=2、B.Health=0、AP=1。
- 文档内容、5 份 Markdown 的 25 个本地链接和格式检查通过；`git diff --check` 通过。

独立 Reviewer Agent `p2_t2_review` 只读审查生产代码、状态契约及新增测试，独立执行 117 项测试和差异空白检查通过；未发现阻断问题或契约偏差，未独立重新构建。主 Agent 在独立审查后重新执行上述构建、测试、只读格式及演示。未执行远端 CI、Unity 或跨运行时验证，当前无未解决的 T2 阻断问题。

### P2-T3 快照文件、摘要与发布（2026-10-29）

实现位于 Headless 的 `Snapshots` 目录，Core/GridGame 本轮未新增文件、JSON、哈希或平台依赖。Program 与 DemoRunner 未变，尚无新 CLI 或回放入口。后续 CLI 可使用内部 SnapshotStore.Save/Load，Load 只返回全部验证成功的候选规则与会话，不主动替换调用方已有引用。

- SnapshotReader/SnapshotJson 严格读取显式字段，拒绝重复/未知/缺失字段、非法 null、错误枚举、非规范 64 位字符串、小数/指数 Int32、无效 UTF-8 和未配对 Unicode 转义。结构读取与领域/兼容校验分开，保持协议错误优先级。
- CanonicalState 按固定字段和集合顺序写出无 BOM、无换行的 UTF-8 字节，生成配置及状态 SHA-256；通过六组 T1 固定字节与摘要向量，不以生产编码器现场生成期望。
- SnapshotEnvironment 读取 runtime、OS、进程架构及三个已加载程序集的内容哈希。SnapshotCodec 校验版本、算法、环境、配置身份、可选预期配置、玩法/会话状态与摘要；完成 `turnIndex - 1 <= completedStepCount` 的跨模块装配检查，避免加法溢出。
- AtomicFilePublisher 使用目标同目录 CreateNew 临时文件，写完关闭后无覆盖发布；发布竞争、写入失败或已有目标不会覆盖原文件，临时文件尽力清理。不创建缺失父目录，不承诺断电耐久性或多文件事务。
- SnapshotException 是宿主内部的具体错误载体，保存 category/code/path/原异常，用于多个文件与编码边界一致分类；没有新增异常基类或通用 Result 框架。InternalsVisibleTo 仅给测试程序集访问内部能力，保持既有公开 API 范围；测试项目复制已有固定样例，不新增依赖。

本轮验收范围为 V3–V6、V11 的快照内部能力及文件行为，并补测默认演示 0–6 步的实际文件保存/恢复与逐步继续结果。文件恢复不依赖原场景文件，旧实例不受失败影响；本轮尚未验证 CLI 退出码、回放差异或跨进程恢复，不能将这些矩阵项整体标为阶段验收完成。

验证环境沿用 macOS 26.6 / arm64、SDK 10.0.202、runtime 10.0.6；基于 `f4bf6ad` 的未提交工作区，Debug 构建。实际检查：

- `dotnet build --no-restore`：0 警告、0 错误。
- `dotnet test --no-build --no-restore`：202 项通过，0 失败、0 跳过；较 T2 新增 85 项，包含六组独立固定向量的字节和哈希断言。
- `dotnet format --verify-no-changes --no-restore`：通过。
- `dotnet run --project src/AiNative.Headless --no-build`：原六步演示通过，最终状态保持不变。
- 文档链接、围栏及空白检查、`git diff --check`：通过。写入失败使用真实临时文件及注入 IOException 的写回调验证，竞争目标在发布前由测试创建；不将这些测试描述为跨文件系统或断电验证。

独立 Reviewer Agent `p2_t3_review` 审查发现并独立复现未配对 Unicode 转义可能抛出未分类 InvalidOperationException。修复限定在 JSON 预遍历的字符串反转义边界映射为 InvalidJson，不扩大领域异常捕获；增加属性名和值、高/低 surrogate 共四项回归。Reviewer 复核修复并对最终构建产物独立执行 202 项测试及差异空白检查通过，当前无未解决阻断问题。主 Agent 已在修复与复核后运行构建、完整测试、只读格式及原演示；远端 CI、CLI、回放、跨进程及跨运行时验收仍未执行。

### P2-T4 外部命令与批处理 CLI（2026-10-29）

Headless 新增 `Cli` 目录，负责严格参数解析、命令文件预校验、run/inspect 装配、检查点和结构化结果。每个 submit/advance 都记录检查点，空推进与业务拒绝保留明确结果，拒绝后继续执行；计数溢出与规则故障按契约区分。snapshot 输入使用内嵌配置，expect-config 只核对身份，输出目标在执行前预检查并沿用 T3 的防覆盖单文件发布。stdout 为一份 JSON 加换行，stderr 为诊断；stdout 失败不会撤销已发布快照。

严格 JSON 读取和分类异常从快照内部辅助类移至 `Protocol`，分别命名为 JsonInput/ProtocolJson 与 HostOperationException，供快照、命令和 CLI 共用，避免重复边界校验。均为 Headless 内部类型，没有增加公共跨模块接口、生产依赖或项目。ScenarioLoader 拆出内部解析入口，支持 CLI 在全部文件结构校验后统一检查版本；原演示仍使用原 Load 语义。新 CLI 额外限制场景阵营为精确 A/B（忽略大小写），不接受枚举转换器的复合或填充字符串。

无参数及单个非保留 token 保持固定演示；run/inspect/replay/--help 为保留入口，`./run` 可作为旧场景路径，`--legacy.json` 这类非保留单 token 仍可使用旧演示。`replay` 与 `--replay-out` 当前明确返回 InvalidArguments，帮助说明未实现，没有进入回放记录或验证。

本轮证据覆盖 V7–V9 与 V11 的命令、CLI 和跨进程部分：固定样例的所有结果字段（除不作机器判定的 message 外）逐项匹配，重复执行 10 次一致；在已完成第 3 步和第 4 步处分别保存，由新进程从另一个工作目录恢复，删除原场景后仍得到相同最终状态、摘要、事件及计数。另覆盖参数互斥、未知/重复参数、完整文件预校验及错误优先级、空操作、末尾未清空队列、业务拒绝、真实规则与序号溢出、配置身份不匹配、环境不匹配、输出预检查、stdout/stderr 故障。T5 的回放和 T6 的整体阶段验收尚未完成。

本地最终检查：

- `dotnet build --no-restore`：0 个警告、0 个错误。
- `dotnet test --no-build --no-restore`：271 项通过，无失败或跳过。
- `dotnet format --verify-no-changes --no-restore`：通过，只读检查，没有整库格式化。
- `dotnet run --project src/AiNative.Headless --no-build`：原固定演示通过，最终 Winner=A、Turn=3、A.Health=2、B.Health=0、AP=1。
- 测试内启动真实进程验证 run/inspect、分段恢复、相对路径和旧入口兼容，stdout/stderr 与退出码分别断言。

独立 Reviewer Agent `p2_t4_review` 发现并复现了两个契约偏差：错误路径使用相对路径，及 `side="A,B"` 被旧枚举转换器意外接受。两项均修复并补回归，Reviewer 对最终生产代码只读复核，独立执行当时的 270 项测试全部通过，并另用真实进程验证两个修复及执行 `git diff --check`。随后主 Agent 将恢复测试扩展为第 3/4 步两个分段点，得到最终 271 项测试，并重新执行上列构建、测试、格式和演示检查。没有未解决阻断问题；远端 CI、回放与跨运行时验收未执行。

文档同步检查：README、设计、规范、阶段记录和协议的本地 Markdown 链接检查通过；最终差异空白检查通过。

### P2-T5 回放记录、验证与首个分歧（2026-10-29）

Headless 新增 `Replays`：ReplayWriter 保存执行前的自包含快照、命令文件和实际检查点；ReplayReader 严格读取联合字段；ReplayValidator 仅验证状态、哈希与调度一致性，不预执行玩法推算期望；CheckpointDifference 比较实际 state/session/result，按单位 ID 和事件数组位置定位，并按字段路径 ordinal 排序。所有类型为 internal，继续使用既有公开管理 API 和单文件发布器，没有新增依赖、项目或 Core/GridGame 文件边界。

`run --replay-out` 只在全部操作正常完成、队列为空时发布记录。快照与回放目标预检互异；按快照、回放顺序发布，后者失败保留并报告已发布快照，stdout 失败也不回滚文件。`replay --input` 可选 expect-config，先完成结构、兼容性和内部一致性验证，再恢复起点逐个执行；业务拒绝和空推进均参与比较。首个分歧立即停止，返回退出码 3 / CheckpointMismatch，保留该步实际检查点及实际 accepted/rejected，comparedCheckpoints 不包括不匹配点。正常匹配返回 ReplayMatched；损坏记录为输入错误，规则故障仍为 SimulationFailure，不冒充回放分歧。

本轮本地证据覆盖 V5/V6/V9/V10 的回放部分，不代表 P2-T6 整体验收完成：

- 固定 replay.json 的记录内容逐项匹配；测试仅绑定当前环境元数据，固定状态、计数、事件和摘要未用执行器重建期望。event-divergence.replay.json 与 divergence-result.json 的全部机器字段匹配。
- 只改命令方向、期望事件、合法状态或随机值均定位首个分歧；相同 stateHash 仍能发现事件或 session 差异。差异覆盖 Changed/Missing/Added、单位 ID 路径、事件数组、完整对象缺失及稳定排序。
- 缺失/多余/乱序检查点、计数/序号/结果种类矛盾、坏 hash、无效状态、配置身份、非法枚举/字段/拒绝事件等在执行前拒绝；submit/empty 必须保持前态。多错误顺序、嵌套路径、版本、环境与配置失败均有回归。
- 真实新进程从新局及中途快照录制，删除原场景、命令和源快照后仍能回放；空回放仅匹配初始点，UInt64 最大随机状态保持。真实规则溢出保存最后可信检查点，未完成记录不发布。
- 输出路径冲突、已有目标、第二文件发布失败及 stdout 故障均有断言；临时文件清理与已发布文件保留符合契约。

最终本地环境：SDK 10.0.202，runtime 10.0.6，macos/arm64；未提交工作区构建的 buildHash 为 `7589dbe1045481241ffdfa1ccabcfca1821cfda284d9e9f8e3bc6a1828e13943`。没有创建提交或把构建身份当作安全签名。

独立 Reviewer Agent `p2_t5_review` 发现并实际复现两类问题：检查点多错误时的索引/计数/状态优先级，以及 PendingCommands 缺少 commandFile 路径前缀。修复后补回归，Reviewer 只读复核、独立运行 332 项测试全部通过，并以真实进程复测修复、匹配和首个命令分歧；没有未解决阻断问题。

审查后主 Agent 执行：

- `dotnet build --no-restore`：0 警告、0 错误。
- `dotnet test --no-build --no-restore`：332 项通过，无失败或跳过。
- `dotnet format --verify-no-changes --no-restore`：通过。此前仅对两个新增回放测试文件做定向格式修正，没有整库格式化。
- 原演示 `dotnet run --project src/AiNative.Headless --no-build` 通过：Winner=A、Turn=3、A.Health=2、B.Health=0、AP=1。
- 临时目录真实进程烟测：录制并匹配 16 个检查点；改第 5 号操作的预期移动事件后返回退出码 3，sequence=2、path=`result.events[0].to.x`，已匹配 6 个点。临时材料已清理。

本轮只同步实际能力、使用说明和任务证据，没有进入 P2-T6、修改 CI 工作流或执行远端 CI；跨平台、跨运行时及 Unity 验收仍未执行。


### P2-T6 集成入口与交付证据（2026-10-30）

本轮基于已合并的 `24a2b2b17cfbca6969ba4cdff788b13d46658299` 创建 `codex/phase-2-acceptance`，没有改变生产规则、公开 API 或文件协议。新增 [验收入口与 V1–V12 证据索引](phase-2-acceptance.md)、Python 标准库脚本和两份固定拒绝输入；现有 C# 测试继续承担随机消费、所有权、故障与编码边界验证。脚本对同一已构建宿主执行 40 次真实 CLI 调用，覆盖固定期望、10 次重复、0–6 步每个保存恢复边界、实际回放、三种业务拒绝以及首个命令/事件分歧。

CI 保留原有 restore/build/test/format/demo，增加 TRX 输出、相同验收脚本及 always 上传证据。没有增加生产依赖或引擎抽象；Python 仅为验证工具。产物写入被 Git 忽略的 TestResults，新输出目录必须不存在，失败时保留已产生材料。

独立 Reviewer Agent `p2_t6_review` 发现恢复点仅与自身保存结果相比较，尤其第 6 步空后缀缺少对照连续运行的断言。修复为前缀每个检查点、保存终点与恢复初始点均直接对照 continuous，再比较后续检查点。Reviewer 最终独立运行 40 个进程通过，并验证已有报告不被覆盖及故障时保存退出码/stdout/stderr；文档映射、链接和差异检查通过，无未解决阻断问题。

实际本地环境：SDK 10.0.202，Python 3.14.7，runtime 10.0.6，macos/arm64；构建 hash `7f78f9f32563c6731cc15353650e768528c59e34f0c2e8cd1780d45291b9204d`。该身份属于本轮未提交工作区使用的构建，不宣称是未来提交/CI 构建的身份。

实际执行与结果：

- `dotnet restore --locked-mode`：成功；审查后 `dotnet build --no-restore`：0 警告、0 错误。
- 审查后 `dotnet test --no-build --no-restore --logger trx --results-directory TestResults/p2-t6-final-unit`：332 项通过，无失败或跳过，TRX 已保存。
- 审查后 `dotnet format --verify-no-changes --no-restore`：通过，没有执行整库格式化。
- 审查后 `python3 scripts/verify_phase2.py --output TestResults/p2-t6-final`：40 次调用全部通过，原演示也在其中。生成的 summary.json 保存每次实际退出码和命令；各次 stdout/stderr、快照和回放在同目录。
- Reviewer 的最终证据位于 `TestResults/p2-t6-review-final/summary.json`；输出目录保护返回 2 且原 summary hash 不变；故意传入非宿主 Core.dll 返回脚本退出码 1，并保留实际宿主失败与输出。
- Python 语法解析、工作流 YAML 解析、Markdown 本地链接及差异空白检查通过。

本地验收时的远端证据边界（后续闭环见下节）：已查询并确认 [PR #3 CI](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40880920093) 成功，日志为 332 项通过、0 失败、0 跳过。它对应 P2-T1–T5，不包含本轮新增流程。P2-T6 的实现和本地验收已完成；本轮改动尚未提交，新 CI 工作流与制品上传尚待远端执行，阶段关闭前仍须补充对应提交的成功 run 与制品证据。不把配置存在、本地脚本通过或旧提交的 CI 成功当成这一步已完成。


### P2-T6 远端 CI 与阶段验收闭环（2026-10-30）

P2-T6 实现已提交为 `17cdf7b9faf651b6758f31caf65927b6e995a218` 并创建 [PR #4](https://github.com/amuroray742-png/ai_native_game_engine/pull/4)。[PR workflow 40991275807](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991275807) 与 [push workflow 40991239106](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991239106) 均为 success，原有 restore/build/test/format/demo、Phase 2 CLI acceptance 和 Preserve validation evidence 全部通过。此次只发布分支和 PR，没有合并。

PR CI 使用 checkout 合并提交 `2f8c4b1b5745c452946f651e5844bab2c086138a`，工作区干净。环境为 SDK 10.0.204、Python 3.14.4、runtime 10.0.14、linux/x64，buildHash 为 `bde6c8f46dd09795ab1b5a5155b27416a3cfd8b94ade8b04071c4a83c353655e`。SDK 按 global.json 的 latestPatch 策略解析；这些环境值来自远端产物，不沿用本地值。

已下载并解析 [制品 12949133349](https://github.com/amuroray742-png/ai_native_game_engine/actions/runs/40991275807/artifacts/12949133349)：名称 `phase-2-evidence-40991275807-1`，归档 153127 字节，展开 117 个文件。TRX 记录 332 项通过、0 失败、0 跳过；summary 为 Passed，40 次 CLI 调用实际退出码全部符合期望。逐一检查各次 stdout/stderr 文件及最终快照、成功回放、命令/事件分歧和损坏输入材料；事件分歧为 operationIndex=5、sequence=2、path=result.events[0].to.x、comparedCheckpoints=6。

[核验元数据](evidence/phase-2/ci-40991275807.json)随仓库保留；完整下载位于本地 `TestResults/remote-40991275807/`，不提交机器绑定产物。远端制品保留 7 天，预计北京时间 2026-11-06 10:23:20 过期。CI 有 Actions Node 运行时及 ubuntu-latest 镜像迁移提示，均非失败；本轮未借此升级无关依赖。

结合此前独立审查、审查后本地验证与本次远端结果，[V1–V12 证据索引](phase-2-acceptance.md)的阶段退出条件已满足，P2-T1–T6 验收完成。该结论限于文档规定的环境和能力，不等于任意运行时/平台互通、Unity 验收或 PR 已合并。此后仅补写验收记录的提交仍由同一 CI 检查，PR 最终检查状态另行确认。
