# Adversarial Runtime Validation Evidence Index

**Status:** Verified evidence archive  
**Status date:** 2026-08-29  
**Scope:** raw xUnit evidence for the complete 36-row deterministic adversarial Runtime Pool matrix.

This document is the provenance index for the canonical [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md). It does not replace the architecture or testing documents; it binds their aggregate validation claims to the archived raw test outputs.

---

## Canonical Evidence Archive

```text
docs/files/adversarial-runtime-validation-logs.zip
SHA-256 = a8e252b2b7277c196d594f0da6963b2e39eab3ad0e2a6415306974d2a8497c03
```

The archive contains 36 independent xUnit outputs arranged as four provider/transport combinations × nine semantic adversarial rows.

[Open the raw evidence archive](../files/adversarial-runtime-validation-logs.zip).

Validation of the archive confirmed:

- 36 non-empty log artifacts;
- 36 distinct SHA-256 hashes;
- exact provider, transport, and `MatrixScenarioId` alignment for every file;
- one frozen `RECURSIVE_CHILD_DAG_PROOF_RESULT` per row;
- `Status=PASS` for every frozen row result;
- two execution cycles per row;
- 36 submitted and 36 completed parent runs per row;
- zero missing recursive child logical steps;
- zero unexpected duplicate recursive child logical steps;
- zero runtime-ownership transition violations;
- `ParentReplay=36/36` for every row;
- `RecursiveChildReplay=NOT_EVALUATED` for every row, preserving the dedicated child-replay non-claim.

---

## Aggregate Evidence

| Metric | Verified aggregate |
|---|---:|
| Matrix rows | 36 / 36 |
| Provider / transport combinations | 4 |
| Execution cycles | 72 |
| Parent runs submitted | 1,296 |
| Parent runs completed | 1,296 |
| Parent logical steps | 66,096 |
| Recursive child executions | 3,888 |
| Recursive child logical steps | 196,992 |
| All executions | 5,184 |
| All logical steps | 263,088 |
| Parent replay proofs | 1,296 / 1,296 |
| Recovered SharedRuns | 288 |
| Missing recursive child steps | 0 |
| Unexpected duplicate recursive child steps | 0 |
| Ownership transition violations | 0 |
| Process-kill identity continuity | 72 / 72 |
| Child-runtime failures | 72 |
| Busy parent-boundary failures | 72 |
| Cumulative xUnit duration | approximately 8 h 16 min 36 s |

Per provider/transport combination:

| Combination | Rows | Approx. xUnit duration |
|---|---:|---:|
| KubernetesPool / gRPC | 9 / 9 | 147.1 min |
| KubernetesPool / HTTP | 9 / 9 | 141.9 min |
| ProcessHostPool / gRPC | 9 / 9 | 101.8 min |
| ProcessHostPool / HTTP | 9 / 9 | 105.8 min |

---

## Frozen-Result Status Note

Some individual raw logs contain campaign-progress fields such as:

```text
AdversarialScheduleMatrix='NOT_YET_VALIDATED'
Matrix='NOT_YET_VALIDATED'
```

or:

```text
AdversarialScheduleMatrix='IN_PROGRESS'
Matrix='IN_PROGRESS'
```

These values record the aggregate matrix state **at the moment that individual row was executed**. They are not the row pass/fail result. The row result is the frozen `Status='PASS'` field in `RECURSIVE_CHILD_DAG_PROOF_RESULT`. The final aggregate matrix status is established by this completed 36-artifact archive.

---

## Row-Level Evidence

### KubernetesPool / gRPC

| Scenario | xUnit test | Duration | SHA-256 |
|---|---|---:|---|
| `baseline` | `Grpc_KubernetesPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.9 min | `8df7d372b2b4478c8e9ae84e052e254ad07abaf2032a08dfee500411832f99bf` |
| `crash-early` | `Grpc_KubernetesPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness` | 16.5 min | `939dcd2a0808b050c5791dedc98862fdff5ac6c16d940df46f904a1beccafaeb` |
| `child-invocation-boundary` | `Grpc_KubernetesPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness` | 16.4 min | `f67df8c5850abef70f1ce32f8030da885798b6e5094398d6314eaa63af31da8b` |
| `continuation-consume` | `Grpc_KubernetesPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness` | 19.5 min | `294bc5a349ca4ff14ea284ea740617fae48c29c727a425fa887e1b43b96f35bd` |
| `depth2-runtime-failure` | `Grpc_KubernetesPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.8 min | `cff0d22fdc0bb7649bf4013cf6cc8c43ef2391c35f5b47b38ac3c349a30a4464` |
| `depth3-runtime-failure` | `Grpc_KubernetesPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 16.8 min | `717320a9397714acca0025d27411adfca910f9bc299a72e32a7afd15ba1997c0` |
| `seed-a` | `Grpc_KubernetesPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.5 min | `cc89d047b8dcac9212013138f7f3dd3ec06dfb1d491b8c7667c2085278168888` |
| `seed-b` | `Grpc_KubernetesPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.3 min | `c34e0a2347059b919b1fd8264eff29a4ea9814e137a04a0c4f6b48da3dddee97` |
| `seed-c` | `Grpc_KubernetesPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.4 min | `25a64b5ec2f4412b2bbc486c253e09234f70ce17769184f50b1636202ced9cd8` |

### KubernetesPool / HTTP

| Scenario | xUnit test | Duration | SHA-256 |
|---|---|---:|---|
| `baseline` | `Http_KubernetesPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness` | 15 min | `07f3f7ec1bb5e43b4a8a1faa850cfc36e856aa0bbd573af7c4597642b181536f` |
| `crash-early` | `Http_KubernetesPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.1 min | `140814291c61fd8803355b92a3c6a633caf7fd4997b69debbe046531f9bf7549` |
| `child-invocation-boundary` | `Http_KubernetesPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.6 min | `2562ee18fd378b4a8d9267ddb53cdd461e792c93543dc9bc014e69785e972bf0` |
| `continuation-consume` | `Http_KubernetesPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness` | 17.2 min | `aa5481728e57b391926d61d81151ddd1cf7c403388ae0d1c642515f562ce1239` |
| `depth2-runtime-failure` | `Http_KubernetesPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.8 min | `b3aaf7e7e58d982f22d1f02b3f0c1f2028a36a98a63fcb082b374fe0aa4b3544` |
| `depth3-runtime-failure` | `Http_KubernetesPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 16.6 min | `5ed5ac926329221372b106010d75f8814219b6dc9313fb15dd5168384099aa0f` |
| `seed-a` | `Http_KubernetesPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.4 min | `d7f05cf01b56c9ac0760e3a787b7d0170cc5f2353d264a472ee14a162f9efb1f` |
| `seed-b` | `Http_KubernetesPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.8 min | `eba4b19c1ecd0519b9afaabce87c68f1a498f7808854d9adf8060d1711cdc8ba` |
| `seed-c` | `Http_KubernetesPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness` | 15.4 min | `5ce4c589d0300857992df0175811c8c2acd08f50a624823eb1035054f1f7f7f6` |

### ProcessHostPool / gRPC

| Scenario | xUnit test | Duration | SHA-256 |
|---|---|---:|---|
| `baseline` | `Grpc_ProcessHostPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.2 min | `79b1fc8a4550c99e071bf1e1574f2b0634597a97dde0497dd746ed25d9eeff10` |
| `crash-early` | `Grpc_ProcessHostPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.1 min | `7088ee6ccab4e116d64debdb24f51d2057e4476852b46464c4b5454273703d04` |
| `child-invocation-boundary` | `Grpc_ProcessHostPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.5 min | `b4311dd88d98816d3b4c54d9a40c1f305bc3a543c8ee666dca122613723723c7` |
| `continuation-consume` | `Grpc_ProcessHostPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.5 min | `210f3cbd4253b69c5b7c3371a3b2a47b6d41cb2746d6c8d6180fd93863a3e7c9` |
| `depth2-runtime-failure` | `Grpc_ProcessHostPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.2 min | `8c9f052ac27c5117618fef7680b3c06d852357c7b5692c7a3f5baab66bde6e91` |
| `depth3-runtime-failure` | `Grpc_ProcessHostPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.2 min | `8bf8a951517f36a52e5232e5855178bd944c397d87716276f61fc4c9dfac7f82` |
| `seed-a` | `Grpc_ProcessHostPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.4 min | `34b7ef7c828ee253659e8a0163f2943f70f69b82809eb61236302041a0304f02` |
| `seed-b` | `Grpc_ProcessHostPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.5 min | `c5e72391e17328ec8a0605f532bd891164616f17615e0da2fbb149463bd76ff1` |
| `seed-c` | `Grpc_ProcessHostPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.2 min | `6bcaeca02ac3dc4f299de49aec3bbefe8c4e27737d97de5be5db31104ac55a2f` |

### ProcessHostPool / HTTP

| Scenario | xUnit test | Duration | SHA-256 |
|---|---|---:|---|
| `baseline` | `Http_ProcessHostPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.4 min | `2d38aba687998cd1515e576f6e8a2217d01c12e1573dfcecf0abfd0483fbc673` |
| `crash-early` | `Http_ProcessHostPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.3 min | `87e215b0bcf5083c006b4e3fca83928bdc7578f2350707429efa5828af75f08f` |
| `child-invocation-boundary` | `Http_ProcessHostPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.6 min | `4b23b974365a477f22ea9ca3548c5439d09b2b255ad30b7cd59314e8242fb3b1` |
| `continuation-consume` | `Http_ProcessHostPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness` | 12.4 min | `91ae81808343a34ae359a65041ef02c694e21f7ec5c4531764e4813e9259990d` |
| `depth2-runtime-failure` | `Http_ProcessHostPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.6 min | `d0cfdb3a3eea77eef8a51c58cd20def7a38b6822aeb4fedbb99f1e854d9a5a9e` |
| `depth3-runtime-failure` | `Http_ProcessHostPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.7 min | `5ad398e9168b6d3253b139d9f3a3d42318d87d1c75f0fec1a2261821a25812fa` |
| `seed-a` | `Http_ProcessHostPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness` | 12 min | `a31a3933e74f23a116123b80522c06be326f0922943b73216c37f7ca61c209d2` |
| `seed-b` | `Http_ProcessHostPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness` | 11.7 min | `db3378b17929100529993b801b5138b2f2d5ebf0e9e2b880eeb151413260abcb` |
| `seed-c` | `Http_ProcessHostPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness` | 12.1 min | `ae3d5ec53f8bd9392e5964b534a8fab325cd4fd68b4a2ea5d435fc6f1ced4323` |

---

## Continuation-Consume Evidence

The continuation-consume row is the narrowest physical failure boundary in the matrix. The final archived ProcessHostPool outputs prove the exact pre-armed physical process is frozen and killed in both execution cycles for both transports.

```text
ProcessHostPool / gRPC:
CONTINUATION CONSUME EXACT PROCESS FROZEN × 2
PhysicalKillProof='PASS' × 2

ProcessHostPool / HTTP:
CONTINUATION CONSUME EXACT PROCESS FROZEN × 2
PhysicalKillProof='PASS' × 2
```

The Kubernetes continuation-consume rows likewise record two exact physical kill proofs per run through the Kubernetes failure boundary.

The durable ownership authority remains the exact continuation `SharedRun` binding rather than a best-effort dispatch signal.

---

## Claim Boundary

This archive proves the selected deterministic matrix rows. It does not claim exhaustive state-space exploration.

The following remain separate proof domains unless and until their own evidence archives are produced:

- recovery-of-recovery / repeated failure during an active recovery chain;
- dedicated recursive-child replay for every nested execution;
- multi-node Kubernetes fault-domain validation;
- multi-control-plane recovery ownership;
- Redis Cluster key-slot and failover validation.

Parent replay is fully included in this matrix. `RecursiveChildReplay=NOT_EVALUATED` is intentionally preserved rather than inferred from the parent replay result.

---

## Related Documents

- [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md)
- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Testing Strategy](testing-strategy.md)
- [Durable Child DAG Composition](child-dag-composition.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
