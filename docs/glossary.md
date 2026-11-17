# Glossary

## SRE and reliability

| Term | Explanation |
|---|---|
| **SRE** | Site Reliability Engineering — Google's approach: operations as an engineering problem, reliability measured and managed through SLOs |
| **SLI** | Service Level Indicator — a measurable quality metric (the share of successful fast requests) |
| **SLO** | Service Level Objective — the target value of an SLI (99% over 30 days) |
| **SLA** | Service Level Agreement — a contractual obligation to a customer, usually softer than the SLO |
| **Error budget** | The permitted "unreliability": 100% − SLO |
| **Burn rate** | The speed at which the error budget is spent; the basis of mature alerts |
| **Golden signals** | Latency, traffic, errors, saturation — the 4 basic signals of a service |
| **MTTR / MTTD** | Mean time to recovery / detection — the average time to recover / to detect |
| **Toil** | Manual, repetitive operational work that is worth automating |
| **Runbook** | Instructions "what to do for such-and-such alert" |
| **Postmortem** | An incident review; blameless — no search for culprits, a search for system causes |
| **On-call** | Being on duty; the person who responds to alerts |
| **Escalation** | Handing a problem to a human (or to the next level) when automation has not coped |

## Kubernetes

| Term | Explanation |
|---|---|
| **kind** | Kubernetes in Docker — a cluster where the "nodes" are Docker containers |
| **Control plane** | The management part of the cluster: apiserver, etcd, scheduler, controller-manager |
| **kubelet** | The agent on a node that starts the containers of pods |
| **Pod** | The smallest unit of execution, one or more containers |
| **Deployment / ReplicaSet** | Management of N identical pods and their updates |
| **StatefulSet** | Pods with a stable identity and their own volumes (for databases) |
| **DaemonSet** | One pod on each node (log collectors, node-exporter) |
| **Job / CronJob** | A one-off / periodic task |
| **Service** | A stable name and balancing for a group of pods |
| **Ingress / Ingress Controller** | HTTP routing rules from outside / the proxy that executes them |
| **Gateway API** | A newer and more expressive replacement for Ingress |
| **ConfigMap / Secret** | Configuration / sensitive configuration (a Secret = base64, not encryption) |
| **PV / PVC / StorageClass** | A volume / a claim for a volume / a storage type with automatic creation |
| **Namespace** | A logical partition of the cluster's resources |
| **Label / Selector** | Labels on objects / selection by labels (this is how a Service finds pods) |
| **Annotation** | Metadata for tools (not for selection) |
| **Probe** | A liveness/readiness/startup check of a container |
| **Requests / Limits** | Guaranteed / maximum resources of a container |
| **OOMKilled** | A container killed for exceeding its memory limit |
| **CrashLoopBackOff** | A container that keeps crashing; k8s restarts it with a growing pause |
| **RBAC** | Role-Based Access Control: ServiceAccount, Role, RoleBinding |
| **NetworkPolicy** | Rules of network access between pods |
| **CNI** | The cluster's network plugin (kindnet, Calico, Cilium) |
| **CRD** | Custom Resource Definition — your own object type in k8s |
| **Operator / Controller** | A program that brings the actual state to the desired one (the reconcile loop) |
| **Reconcile loop** | The cycle "compare the desired and the actual → take a step" |
| **Helm / Chart / Release / Values** | The package manager / a package / an installed instance / parameters |
| **GitOps / Argo CD** | The cluster is synchronized with the state in git |

## Network and security

| Term | Explanation |
|---|---|
| **TLS** | Channel encryption; the client verifies the server |
| **mTLS** | Mutual TLS: both sides verify each other's certificates |
| **cert-manager** | An operator that issues and renews certificates in k8s |
| **Service mesh** | A layer of proxies next to each service: mTLS, metrics, retries, routing |
| **Sidecar** | An extra container in a pod (for example, a mesh proxy) |
| **Linkerd / Istio** | A service mesh: simple and light / powerful and complex |
| **Native sidecar** | An init container with `restartPolicy: Always`: starts before the application, stops after it |
| **Trust anchor** | The root certificate of the mesh; the proxies trust everything issued from it |
| **Workload identity** | "Who this service is", confirmed by a certificate (in Linkerd — by ServiceAccount), not by IP or a label |
| **Opaque port** | A port whose traffic the mesh does not try to parse as HTTP (in Linkerd by default, for example, 5432) |
| **GAMMA** | The Gateway API approach for "service → service" traffic: an HTTPRoute with a Service as the parent, not a Gateway |
| **Retry storm** | Retries of requests to a sick service multiply its load and prevent it from recovering |
| **Zero trust** | Not trusting traffic just because it is "inside the network" |
| **Blast radius** | The scale of the possible damage from an action or a failure |

## Observability

| Term | Explanation |
|---|---|
| **Prometheus** | A system for collecting and storing metrics (pull model) |
| **Scrape** | Prometheus polling a service's `/metrics` |
| **PromQL / LogQL** | The query languages of Prometheus / Loki |
| **ServiceMonitor** | A CRD: "scrape such-and-such services" |
| **Exporter** | A program that exposes metrics of something in the Prometheus format |
| **Alertmanager** | Routing, grouping, suppression of alerts |
| **Loki** | A log store with an index by labels |
| **Grafana** | Visualization of metrics and logs |
| **OpenTelemetry (OTel)** | A standard and SDK for metrics, logs and traces |
| **Trace / Span** | The path of a request through the services / one section of it |
| **Cardinality** | The number of unique label combinations; a high one kills Prometheus |

## Chaos engineering

| Term | Explanation |
|---|---|
| **Steady state** | The measurable "normal" of the system |
| **Hypothesis** | An expectation of how the system will survive a failure |
| **Fault injection** | Introducing a failure |
| **Chaos Mesh** | A Kubernetes-native chaos experiment tool based on CRDs |
| **Game day** | A planned session of experiments with the team |

## AI agents

| Term | Explanation |
|---|---|
| **LLM** | Large Language Model |
| **Agent harness** | The code around the LLM: triggers, context, tools, the loop, limits, the journal |
| **Tool / function calling** | A mechanism where the model asks for a function call with structured arguments |
| **ReAct** | The pattern "reasoning → action → observation → …" |
| **Ralph loop** | The outer loop of the agent: the same task again and again, each time with a fresh context, until code confirms the task is solved (or the limits run out) |
| **RALF** | One iteration of the Ralph loop in this project: Retrieve → Analyze → Lead → Feedback |
| **RunState** | The progress file between Ralph loop iterations — the agent's memory outside the model's context |
| **Completion check** | A deterministic "task solved" check that does not depend on the model's opinion |
| **OODA loop** | Observe–Orient–Decide–Act — a military decision-making model, close in spirit |
| **Context window** | The maximum amount of text (in tokens) the model sees at once |
| **Token** | A unit of text for the model (≈ 3–4 characters of English text) |
| **Guardrails** | Deterministic constraints on what the agent may do |
| **Human-in-the-loop** | A human confirms the agent's actions |
| **Prompt injection** | An attack: instructions for the model hidden in data (for example, in logs) |
| **Evals** | A set of scenarios with expected results for assessing the agent's quality |
| **OpenAI-compatible API** | The `/v1/chat/completions` protocol, supported by many providers and by LM Studio |
| **LM Studio** | An application for running LLMs locally with an OpenAI-compatible server |
| **Microsoft.Extensions.AI** | .NET abstractions (`IChatClient`) over different LLM providers |
| **ValidatingAdmissionPolicy** | CEL rules that the API server checks for each request (without a webhook); here — "the agent may only restart" |
| **Server-side dry run** | `dryRun=All`: the API server checks the request (RBAC, admission) and changes nothing |
| **Kill switch** | An instant off-switch for the agent's actions without a redeploy (here — the ConfigMap `sre-agent-kill-switch`) |
| **Circuit breaker** | After N failed attempts, stop trying and escalate |
| **Blast radius** | How much a failure or an experiment can touch in total; limited by selectors, namespaces, `duration` |
| **Fault injection** | Deliberately introducing a failure (latency, errors, a leak) to test a hypothesis |
| **Smoke test** | A quick check that "the essentials work" after a deployment or a restart |
| **Post hoc ergo propter hoc** | "After — therefore because of": the error of taking an improvement that came after an action as its result |
| **Scorecard** | A table of the agent's grades by game-day scenario |
