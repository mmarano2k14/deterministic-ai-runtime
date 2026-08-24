# Local Kubernetes and Minikube Environment

**Status:** Validated local integration environment for KubernetesPool production scenarios.

This document explains how to install and recover the local Kubernetes environment used by the .NET production/integration scenarios, how to build the runtime container image, how to load it into Minikube, and how to verify the complete environment before a long Runtime Pool validation.

Minikube provides the local Kubernetes cluster. Docker Desktop provides the container driver and the local image build environment.

---

## 1. Source of Truth for the Runtime Image

Do not treat a documentation tag as permanent configuration.

The authoritative image name used by the Kubernetes integration scenarios is defined in:

```text
implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Scenarios\Production\Providers\Base\KubernetesSdkScenarioConstants.cs
```

From the `implementations\dotnet` working directory, the same file is:

```text
Tests\Multiplexed.AI.McpServer.Tests.Integration\Scenarios\Production\Providers\Base\KubernetesSdkScenarioConstants.cs
```

The relevant contract is:

```csharp
KubernetesSdkScenarioConstants.RuntimeImage
KubernetesSdkScenarioConstants.ImagePullPolicy
KubernetesSdkScenarioConstants.Namespace
```

The current documented example uses:

```text
RuntimeImage    = multiplexed-ai-runtime:k8s-debug-131
ImagePullPolicy = Never
Namespace       = ai-runtime
```

If `RuntimeImage` changes in source, build and load the exact new value. Do not keep using `k8s-debug-131` simply because it appears in this tutorial.

Before building, verify the current values directly from source:

```powershell
Select-String `
  -Path .\implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Scenarios\Production\Providers\Base\KubernetesSdkScenarioConstants.cs `
  -Pattern 'RuntimeImage|ImagePullPolicy|Namespace|GatewayName|GatewayPort|MongoDatabaseName'
```

---

## 2. Dockerfile Source

The runtime image is built from:

```text
implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile
```

The current Dockerfile is a multi-stage .NET 10 build:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet publish implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Multiplexed.AI.McpServer.Host.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
EXPOSE 50051

ENTRYPOINT ["dotnet", "Multiplexed.AI.McpServer.Host.dll"]
```

Because the Dockerfile copies repository content using paths rooted at `implementations/dotnet/...`, the Docker build context must be the **repository root**.

---

## 3. Install the Local Kubernetes Toolchain on Windows

The integration environment uses **Minikube** as the local Kubernetes cluster and **Docker Desktop** as the container driver. `kubectl` is the Kubernetes CLI used by the scenarios and diagnostics.

### 3.1 Windows and virtualization prerequisites

For the Docker Desktop + Minikube path used here:

- hardware virtualization must be enabled in BIOS/UEFI;
- WSL 2 is the recommended Docker Desktop backend for this local Linux-container workflow;
- Docker Desktop must be running before Minikube is started with `--driver=docker`;
- allow enough host resources to dedicate the validated **8 CPU / 12 GiB** Minikube profile described below.

Check WSL first:

```powershell
wsl --version
```

If WSL is not installed or is stale, from an elevated PowerShell:

```powershell
wsl --install
wsl --update
```

A restart may be required after first-time WSL enablement.

### 3.2 Install Docker Desktop

Install Docker Desktop using the official Windows installer and select/use the WSL 2 backend for Linux containers. After installation, start Docker Desktop and wait until the engine is ready.

Verify:

```powershell
docker version
docker info
```

Official installation reference:

- <https://docs.docker.com/desktop/setup/install/windows-install/>

### 3.3 Install `kubectl`

The official Kubernetes Windows documentation supports `winget`:

```powershell
winget install -e --id Kubernetes.kubectl
```

Close/reopen PowerShell if PATH was changed, then verify:

```powershell
kubectl version --client
```

Keep the `kubectl` client within one Kubernetes minor version of the cluster when possible. The documented Minikube profile below pins Kubernetes `v1.35.1`.

Official reference:

- <https://kubernetes.io/docs/tasks/tools/install-kubectl-windows/>

### 3.4 Install Minikube

The official Minikube Windows installation supports Windows Package Manager:

```powershell
winget install Kubernetes.minikube
```

Close/reopen PowerShell if needed, then verify:

```powershell
minikube version
```

Official reference:

- <https://minikube.sigs.k8s.io/docs/start/>

### 3.5 Verify the repository build toolchain

The runtime container is built by the .NET 10 SDK stage in the Dockerfile. Local integration tests also require the repository-compatible .NET SDK.

Verify all required tools before continuing:

```powershell
docker version
kubectl version --client
minikube version
dotnet --info
```

---

## 4. Build the Runtime Image

Open PowerShell at the repository root.

For the current documented image value:

```powershell
docker build `
  -f .\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile `
  -t multiplexed-ai-runtime:k8s-debug-131 `
  .
```

Equivalent one-line command:

```powershell
docker build -f .\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile -t multiplexed-ai-runtime:k8s-debug-131 .
```

Verify the image exists in Docker Desktop:

```powershell
docker images | Select-String "multiplexed-ai-runtime"
```

If `KubernetesSdkScenarioConstants.RuntimeImage` has changed, replace the tag in both commands with the exact source value.

---

## 5. What `minikube delete` Removes

Running:

```powershell
minikube delete -p minikube
```

removes the whole local Kubernetes cluster, including:

- the `ai-runtime` namespace;
- runtime Pods;
- Services;
- Gateway resources;
- `HTTPRoute` and `GRPCRoute` resources;
- Gateway API CRDs installed in that cluster;
- Envoy Gateway;
- the `GatewayClass`;
- images loaded into Minikube;
- Kubernetes cluster state;
- resources created by previous integration tests.

It does **not** delete Redis or MongoDB running on the Windows host.

After a cluster deletion, never assume the Kubernetes prerequisites or loaded runtime image still exist.

---

## 6. Current Local Test Contract

The current documented local contract is:

```text
Namespace            = ai-runtime
RuntimeImage         = multiplexed-ai-runtime:k8s-debug-131
ImagePullPolicy      = Never

GatewayName          = ai-runtime-gateway
GatewayListenerName  = runtime
GatewayPort          = 8080

GatewayClassName     = eg
GatewayController    = gateway.envoyproxy.io/gatewayclass-controller

Redis                = host.minikube.internal:6379
MongoDB              = host.minikube.internal:27017
Mongo database       = multiplexed_ai_tests

Redis host-side port = 6379
Mongo host-side port = 27017
```

Because `ImagePullPolicy = Never`, Kubernetes will not fetch the runtime image from a registry. The exact image must already exist inside Minikube.

---

## 7. Recommended Minikube Resources

For large Runtime Pool scenarios, avoid the historical local profile:

```text
2 CPU
8 GiB RAM
```

A `5×5` KubernetesPool topology can involve:

```text
5 Pod hosts
25 RuntimeInstance processes
30 .NET processes total
+
Kubernetes control plane
```

The undersized profile previously exhibited:

```text
Minikube memory near saturation
CPU quota saturation
high load average
Redis timeouts
MongoDB connectivity degradation
kubectl HTTP/2 disconnects
kube-apiserver TLS handshake timeouts
```

Recommended local validation profile:

```text
8 CPU
12 GiB RAM
```

---

## 8. Create a Fresh Minikube Cluster

Remove a stale profile when a clean rebuild is required:

```powershell
minikube stop
minikube delete -p minikube
```

Create the validated local profile:

```powershell
minikube start `
  -p minikube `
  --driver=docker `
  --cpus=8 `
  --memory=12288 `
  --kubernetes-version=v1.35.1
```

Wait for Minikube to report completion.

Verify that `kubectl` is targeting the Minikube profile before applying or deleting resources:

```powershell
kubectl config current-context
kubectl cluster-info
kubectl get nodes -o wide
```

Expected current context:

```text
minikube
```

---

## 9. Verify the Real Docker cgroup Limits

Do not rely only on the Kubernetes node description.

The outer Minikube Docker container can have a stricter cgroup quota than the host topology suggests.

Run:

```powershell
docker inspect minikube --format "Memory={{.HostConfig.Memory}} MemorySwap={{.HostConfig.MemorySwap}} NanoCpus={{.HostConfig.NanoCpus}}"
```

Expected values for the recommended profile:

```text
Memory=12884901888
MemorySwap=12884901888
NanoCpus=8000000000
```

Equivalent to:

```text
12 GiB RAM
8 CPU
```

Do not run the large KubernetesPool scenario if the old 2-CPU / 8-GiB limits are still active.

---

## 10. Create the Required Namespace

The production tests expect the namespace to exist.

Create it:

```powershell
kubectl create namespace ai-runtime
```

Verify:

```powershell
kubectl get namespace ai-runtime
```

Expected:

```text
NAME         STATUS
ai-runtime   Active
```

A missing namespace can produce scale-out requests without any Pod creation.

---

## 11. Install Gateway API and Envoy Gateway

A new Minikube cluster does not contain the Gateway API CRDs or Envoy Gateway controller used by the scenarios.

Validated controller baseline:

```text
Envoy Gateway v1.8.2
GatewayClass = eg
Controller = gateway.envoyproxy.io/gatewayclass-controller
```

Install:

```powershell
kubectl apply --server-side `
  -f https://github.com/envoyproxy/gateway/releases/download/v1.8.2/install.yaml
```

Wait for the controller:

```powershell
kubectl wait `
  --timeout=5m `
  -n envoy-gateway-system `
  deployment/envoy-gateway `
  --for=condition=Available
```

Verify:

```powershell
kubectl get pods -n envoy-gateway-system -o wide
```

The Envoy Gateway Pod should be `1/1 Running`.

---

## 12. Verify Gateway API Resources

Run:

```powershell
kubectl api-resources | Select-String "GatewayClass|Gateway|HTTPRoute|GRPCRoute"
```

The cluster must expose at least:

```text
GatewayClass
Gateway
HTTPRoute
GRPCRoute
```

Verify the CRDs as well:

```powershell
kubectl get crd | Select-String "gateway.networking.k8s.io"
```

---

## 13. Ensure the `eg` GatewayClass Exists

Check:

```powershell
kubectl get gatewayclass
```

If `eg` is missing:

```powershell
@"
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: eg
spec:
  controllerName: gateway.envoyproxy.io/gatewayclass-controller
"@ | kubectl apply -f -
```

Verify:

```powershell
kubectl get gatewayclass
```

Do not proceed until `eg` is accepted.

---

## 14. Load the Runtime Image into Minikube

After building the image in Docker Desktop, load the exact source-defined image into Minikube.

Current example:

```powershell
minikube image load multiplexed-ai-runtime:k8s-debug-131
```

Verify:

```powershell
minikube image ls | Select-String "multiplexed-ai-runtime"
```

Output may appear as:

```text
docker.io/library/multiplexed-ai-runtime:k8s-debug-131
```

That normalized name is valid.

With `ImagePullPolicy = Never`, an absent image is a hard environment error; the test will not pull it automatically.

---

## 15. Verify Redis Connectivity from Minikube

Runtime Pods use:

```text
host.minikube.internal:6379
```

Verify from the Minikube node:

```powershell
minikube ssh -- "nc -zv host.minikube.internal 6379"
```

Expected:

```text
Connection to host.minikube.internal (...) 6379 port [tcp/redis] succeeded!
```

If it fails, verify the host-side Redis/Memurai service before running the scenario.

---

## 16. Verify MongoDB Connectivity from Minikube

Runtime Pods use:

```text
mongodb://host.minikube.internal:27017/?directConnection=true
```

Verify:

```powershell
minikube ssh -- "nc -zv host.minikube.internal 27017"
```

Do not start the production scenario until this route succeeds.

---

## 17. Complete Pre-Flight Check

Run this block before a long Kubernetes Runtime Pool validation:

```powershell
Write-Host ""
Write-Host "===== MINIKUBE CONTAINER LIMITS ====="
docker inspect minikube --format "Memory={{.HostConfig.Memory}} MemorySwap={{.HostConfig.MemorySwap}} NanoCpus={{.HostConfig.NanoCpus}}"

Write-Host ""
Write-Host "===== MINIKUBE STATUS ====="
minikube status

Write-Host ""
Write-Host "===== NAMESPACE ====="
kubectl get namespace ai-runtime

Write-Host ""
Write-Host "===== GATEWAY CLASS ====="
kubectl get gatewayclass

Write-Host ""
Write-Host "===== ENVOY GATEWAY ====="
kubectl get pods -n envoy-gateway-system

Write-Host ""
Write-Host "===== GATEWAY API ====="
kubectl api-resources | Select-String "GatewayClass|Gateway|HTTPRoute|GRPCRoute"

Write-Host ""
Write-Host "===== RUNTIME IMAGE ====="
minikube image ls | Select-String "multiplexed-ai-runtime"

Write-Host ""
Write-Host "===== REDIS ====="
minikube ssh -- "nc -zv host.minikube.internal 6379"

Write-Host ""
Write-Host "===== MONGO ====="
minikube ssh -- "nc -zv host.minikube.internal 27017"

Write-Host ""
Write-Host "===== CURRENT AI-RUNTIME RESOURCES ====="
kubectl get pod,svc,gateway,grpcroute -n ai-runtime -o wide
```

A clean cluster may legitimately report no current resources in `ai-runtime`; the namespace itself must still exist.

---

## 18. Expected Ready State

Before starting a production scenario:

```text
Docker Desktop                Running
Minikube Docker driver        Running
Minikube CPU quota            8 CPU
Minikube memory               12 GiB

ai-runtime namespace          Active

Gateway API CRDs              Present
GatewayClass eg               Accepted
Envoy Gateway                 1/1 Running

Runtime image                 exact RuntimeImage present in Minikube

Redis from Minikube           Reachable
MongoDB from Minikube         Reachable

Runtime Pods                  0 before a clean scenario
```

---

## 19. Watch Runtime Pods

Open a separate PowerShell terminal:

```powershell
kubectl get pod -n ai-runtime -w
```

Expected transitions include:

```text
Pending
ContainerCreating
Running
1/1 Running
```

If scale-out is requested but the watch stays empty, verify the namespace immediately.

---

## 20. Monitor Minikube During Large Scenarios

Run:

```powershell
docker stats minikube
```

Monitor:

```text
CPU %
MEM USAGE / LIMIT
MEM %
PIDS
NET I/O
```

With an 8-CPU quota, Docker can report close to `800%` CPU at full quota usage.

---

## 21. Optional Host and Minikube Telemetry Logger

```powershell
while ($true) {

    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

    $os = Get-CimInstance Win32_OperatingSystem

    $usedGB = [math]::Round(
        ($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB,
        2
    )

    $freeGB = [math]::Round(
        $os.FreePhysicalMemory / 1MB,
        2
    )

    "$ts UsedRAM=${usedGB}GB FreeRAM=${freeGB}GB" |
        Out-File C:\5x5-host-memory.log -Append

    docker stats minikube --no-stream --format `
        "$ts CPU={{.CPUPerc}} MEM={{.MemUsage}} NET={{.NetIO}} PIDS={{.PIDs}}" |
        Out-File C:\5x5-minikube-stats.log -Append

    Start-Sleep -Seconds 10
}
```

Stop with `Ctrl+C`.

---

## 22. Capture Diagnostics Before Restarting an Unresponsive Cluster

Typical infrastructure-starvation symptoms include:

```text
http2: client connection lost
TLS handshake timeout
kubectl unable to connect
RedisTimeoutException
RedisConnectionException
Mongo SocketException
runtime leases disappearing
```

Do not restart Minikube immediately. Capture the live state first:

```powershell
$outDir = "C:\k8s-crash-live"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

kubectl get nodes -o wide 2>&1 |
    Out-File "$outDir\kubectl-nodes.txt"

kubectl get pods -A -o wide 2>&1 |
    Out-File "$outDir\kubectl-pods.txt"

kubectl get events -A --sort-by=.lastTimestamp 2>&1 |
    Out-File "$outDir\kubectl-events.txt"

minikube status 2>&1 |
    Out-File "$outDir\minikube-status.txt"

docker inspect minikube 2>&1 |
    Out-File "$outDir\docker-inspect-minikube.json"

docker stats minikube --no-stream 2>&1 |
    Out-File "$outDir\docker-stats-minikube.txt"

docker exec minikube sh -c "date; uptime; free -h; df -h; df -i" 2>&1 |
    Out-File "$outDir\minikube-resources.txt"

docker exec minikube sh -c "ps aux" 2>&1 |
    Out-File "$outDir\minikube-processes.txt"

docker exec minikube sh -c "crictl ps -a" 2>&1 |
    Out-File "$outDir\crictl-ps-a.txt"

docker exec minikube sh -c `
    "journalctl -u kubelet --since '30 minutes ago' --no-pager" 2>&1 |
    Out-File "$outDir\kubelet-last-30m.txt"

docker exec minikube sh -c `
    "dmesg -T | tail -n 500" 2>&1 |
    Out-File "$outDir\dmesg-last-500.txt"
```

Restart only after diagnostic capture is complete.

---

## 23. Common Failure Checklist

### No Pods appear after scale-out

```powershell
kubectl get namespace ai-runtime
```

Create the namespace if missing, then restart the scenario from the beginning.

### `GatewayClass` resource type does not exist

Reinstall Envoy Gateway / Gateway API CRDs:

```powershell
kubectl apply --server-side `
  -f https://github.com/envoyproxy/gateway/releases/download/v1.8.2/install.yaml
```

### Gateway API exists but `eg` is missing

Create the `GatewayClass` using the manifest in section 13.

### Pod reports an unavailable image with `ImagePullPolicy=Never`

First verify the exact source value in `KubernetesSdkScenarioConstants.RuntimeImage`, then rebuild/load that same tag:

```powershell
docker images | Select-String "multiplexed-ai-runtime"
minikube image load multiplexed-ai-runtime:k8s-debug-131
minikube image ls | Select-String "multiplexed-ai-runtime"
```

### Redis or MongoDB times out immediately

```powershell
minikube ssh -- "nc -zv host.minikube.internal 6379"
minikube ssh -- "nc -zv host.minikube.internal 27017"
```

### Large scenario destabilizes Kubernetes API

Check actual container limits before changing application timeouts:

```powershell
docker stats minikube --no-stream
docker inspect minikube --format "Memory={{.HostConfig.Memory}} MemorySwap={{.HostConfig.MemorySwap}} NanoCpus={{.HostConfig.NanoCpus}}"
```

Do not compensate for infrastructure starvation by increasing correctness timeouts.

---

## 24. Fresh-Cluster Bootstrap Sequence

From the repository root, build the image first:

```powershell
docker build -f .\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile -t multiplexed-ai-runtime:k8s-debug-131 .
```

Then bootstrap Minikube:

```powershell
minikube stop
minikube delete -p minikube

minikube start `
  -p minikube `
  --driver=docker `
  --cpus=8 `
  --memory=12288 `
  --kubernetes-version=v1.35.1

kubectl create namespace ai-runtime

kubectl apply --server-side `
  -f https://github.com/envoyproxy/gateway/releases/download/v1.8.2/install.yaml

kubectl wait `
  --timeout=5m `
  -n envoy-gateway-system `
  deployment/envoy-gateway `
  --for=condition=Available

@"
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: eg
spec:
  controllerName: gateway.envoyproxy.io/gatewayclass-controller
"@ | kubectl apply -f -

minikube image load multiplexed-ai-runtime:k8s-debug-131

docker inspect minikube --format "Memory={{.HostConfig.Memory}} MemorySwap={{.HostConfig.MemorySwap}} NanoCpus={{.HostConfig.NanoCpus}}"

kubectl get namespace ai-runtime
kubectl get gatewayclass
kubectl get pods -n envoy-gateway-system

minikube image ls | Select-String "multiplexed-ai-runtime"

minikube ssh -- "nc -zv host.minikube.internal 6379"
minikube ssh -- "nc -zv host.minikube.internal 27017"
```

Then open a Pod watch in a separate terminal:

```powershell
kubectl get pod -n ai-runtime -w
```

---

## 25. Image-Tag Change Procedure

Whenever the runtime image version changes:

```text
1. Read KubernetesSdkScenarioConstants.RuntimeImage.
2. Build the Dockerfile with exactly that tag.
3. Verify the image in Docker Desktop.
4. Load exactly that tag into Minikube.
5. Verify it with minikube image ls.
6. Remove stale ai-runtime Pods before re-running if they were created with an older tag.
```

Example:

```powershell
$runtimeImage = "multiplexed-ai-runtime:k8s-debug-131" # copy exact current source value

docker build `
  -f .\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile `
  -t $runtimeImage `
  .

minikube image load $runtimeImage
minikube image ls | Select-String "multiplexed-ai-runtime"
```

The source constant, Docker tag, and Minikube-loaded image must always agree.

---

## 26. Related Documents

- [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md)
- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Testing Strategy](testing-strategy.md)
- [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md)
