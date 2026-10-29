# Kubernetes

recruIT's Helm chart is published to the repository's OCI registry at `oci://ghcr.io/open-recruit/recruit/charts/recruit`.
Every [release](https://github.com/open-recruit/recruit/releases) also has it attached as `recruit-helm-chart.tgz`.

The chart deploys:

- the screening list (`list`), together with a Job that migrates its database ahead of every new version
- the query module (`querySqlOnFhir`), which is disabled by default since it needs a Trino to query
- a Job that installs recruIT's FHIR SearchParameters on every install and upgrade

and, unless disabled, the dependencies those need:

- a [HAPI FHIR JPA server](https://github.com/hapifhir/hapi-fhir-jpaserver-starter) with its own PostgreSQL
  database (`fhirserver`)
- a PostgreSQL database for the screening list (`postgres`)
- [MailDev](https://github.com/maildev/maildev), a mock mail server that catches notification emails (`maildev`)

For demos, the chart can also generate [Synthea](https://synthetichealth.github.io/synthea/) patients and a sample
study (`syntheticPatients`, `sampleStudy`). The [Quickstart](../getting-started/installation.md) uses these to get
a demo running with a single command.

!!! info "Documentation for all available chart options"

    You can find a complete description of all chart values in the
    [chart's README](https://github.com/open-recruit/recruit/blob/master/charts/recruit/README.md#values).

## Prerequisites

- a Kubernetes cluster, version 1.30 or later
- [kubectl](https://kubernetes.io/docs/tasks/tools/) and [Helm](https://helm.sh/docs/intro/install/) 3.8 or later
- an ingress controller to make the screening list reachable for your users

## Production installation

For production, replace the mock mail server with your own SMTP server and connect the modules to the services
listed in [Docker Compose](./compose.md#production-installation): the FHIR server, Trino with a FHIR lakehouse, an
OpenID Connect provider, and optionally a Pathling server. That page also explains what each of them needs to provide
and how they work together.

You can keep the included HAPI FHIR server and PostgreSQL databases, or replace them with your own. For highly
available PostgreSQL clusters run inside Kubernetes, see [CloudNativePG for HA databases](#cloudnativepg-for-ha-databases).

Start from the following values, which connect recruIT to external services only:

```yaml title="values-recruit-production.yaml"
--8<-- "_snippets/k8s/values-recruit-production.yaml"
```

The values refer to Secrets instead of containing credentials. Create them before installing the chart:

```sh
kubectl create namespace recruit

kubectl create secret generic -n recruit recruit-list-db --from-literal=password=change-me
kubectl create secret generic -n recruit recruit-list-oidc --from-literal=client-secret=change-me
kubectl create secret generic -n recruit recruit-list-smtp --from-literal=smtp-password=change-me
kubectl create secret generic -n recruit recruit-query-trino --from-literal=password=change-me
```

Then install the chart:

<!-- x-release-please-start-version -->

```sh
helm upgrade --install -n recruit \
  --version 11.1.0 \
  -f values-recruit-production.yaml \
  --wait \
  recruit oci://ghcr.io/open-recruit/recruit/charts/recruit
```

<!-- x-release-please-end-version -->

Register `https://recruit.example.com/signin-oidc` as the redirect URI of the `recruit-list` client with your OpenID
Connect provider, see [Authentication](../configuration/options.md#authentication). Sign-in only works over HTTPS, so
the ingress needs a TLS certificate, e.g. issued by [cert-manager](https://cert-manager.io/) to the
`recruit-list-tls` Secret.

To make sure the modules started and can reach the FHIR server, run the chart's tests:

```sh
helm test -n recruit recruit
```

On every scheduled run, the query module logs `Polling for ResearchStudy resources referencing a Group with SQLQuery
Library`, followed by the studies it found and processed:

```sh
kubectl logs -n recruit deploy/recruit-query-sql-on-fhir --follow
```

### Settings the chart doesn't expose

The chart sets the most important [configuration options](../configuration/options.md) from its values. Set any other
option as an environment variable via `list.extraEnv` or `querySqlOnFhir.extraEnv`, e.g. to only recommend patients
who meet every criterion:

```yaml
querySqlOnFhir:
  extraEnv:
    - name: QUERY_SQL_ON_FHIR_REQUIRE_ALL_CRITERIA_MET
      value: "true"
```

### Database migrations

Before a new version of the screening list starts, a Job named `recruit-list-migrate-<hash>` updates its database
schema. The hash is derived from the image tag, so the Job only runs once per version, and is deleted 10 minutes after
it finished (`list.migrationJob.ttlSecondsAfterFinished`). The screening list's pods wait for it in an init container,
which asks the Kubernetes API whether the Job has completed. The chart creates a Role that allows just that.

If the migration fails, the screening list's pods don't start. Check the Job's logs:

```sh
kubectl logs -n recruit -l app.kubernetes.io/component=list-migrate --tail=-1
```

Since the Job isn't a Helm hook, this also works when the chart is rendered with `helm template` by a GitOps tool
like [Argo CD](https://argo-cd.readthedocs.io/) or [Flux](https://fluxcd.io/).

## Example installation using KinD

This walks you through installing recruIT with the demo fixtures on your local machine using
[KinD](https://kind.sigs.k8s.io/), with:

- a multi-node cluster to demonstrate topology zone aware pod spreading, see [High availability](#high-availability)
- the screening list and the FHIR server exposed via the [Traefik](https://traefik.io/) ingress controller on
  [nip.io](https://nip.io) domains resolving to localhost
- the restricted [Pod Security Standard](https://kubernetes.io/docs/concepts/security/pod-security-standards/)
  enforced, which all components comply with

It needs a machine with around 16 GiB of memory.

First, save the following as `kind-config.yaml` and create the cluster:

```yaml title="kind-config.yaml"
--8<-- "_snippets/k8s/kind-config.yaml"
```

```sh
kind create cluster --config=kind-config.yaml
```

Install Traefik, with its HTTP and HTTPS ports published on the NodePorts mapped to your host's ports 80 and 443
above:

```sh
helm upgrade --install -n traefik --create-namespace \
  --set service.spec.type=NodePort \
  --set ports.web.nodePort=30080 \
  --set ports.websecure.nodePort=30443 \
  --wait \
  traefik oci://ghcr.io/traefik/helm/traefik
```

Create a namespace for recruIT that enforces the restricted Pod Security Standard:

```sh
kubectl create namespace recruit
kubectl label namespace recruit \
  pod-security.kubernetes.io/enforce=restricted \
  pod-security.kubernetes.io/enforce-version=latest
```

Save the following as `values-kind-recruit.yaml`. It enables the ingresses of the screening list and the FHIR server:

```yaml title="values-kind-recruit.yaml"
--8<-- "_snippets/k8s/values-kind-recruit.yaml"
```

And finally, install the chart using these values on top of the chart's
[`values-demo.yaml`](https://github.com/open-recruit/recruit/blob/master/charts/recruit/values-demo.yaml), which is
explained in the [Quickstart](../getting-started/installation.md):

<!-- x-release-please-start-version -->

```sh
helm upgrade --install -n recruit \
  --render-subchart-notes \
  --version 11.1.0 \
  -f https://raw.githubusercontent.com/open-recruit/recruit/refs/tags/v11.1.0/charts/recruit/values-demo.yaml \
  -f values-kind-recruit.yaml \
  --wait \
  --timeout 30m \
  recruit oci://ghcr.io/open-recruit/recruit/charts/recruit
```

<!-- x-release-please-end-version -->

The install takes several minutes to download the images, generate the synthetic patients, and load them into the FHIR
server. Once the command exits, these services are reachable:

| Service                | URL                                                 |
| ---------------------- | --------------------------------------------------- |
| recruIT Screening List | <http://recruit-list.127.0.0.1.nip.io/>             |
| HAPI FHIR Server       | <http://recruit-fhir-server.127.0.0.1.nip.io/fhir/> |

The query module checks for studies every minute, so the sample study's screening list shows up about a minute later:

![The screening list of the sample study](../_img/screenshots/hero/screening-list-light.png#only-light)
![The screening list of the sample study](../_img/screenshots/hero/screening-list-dark.png#only-dark)

MailDev doesn't have an ingress. To read the notification emails it caught, forward its port and open
<http://127.0.0.1:1080>:

```sh
kubectl port-forward -n recruit svc/recruit-maildev-web 1080:1080
```

See [Notifications](../configuration/notifications.md) for how to subscribe to a study and trigger a notification. To
create additional studies, follow [Creating your first study](../getting-started/creating-your-first-study.md).

To remove everything again, delete the cluster:

```sh
kind delete cluster
```

## Metrics

All modules expose metrics in Prometheus format (see [Observability](./observability.md)). The chart makes it easy to scrape
these metrics by integrating with the widely used [Prometheus Operator](https://prometheus-operator.dev/):

```sh
helm upgrade --install -n monitoring --create-namespace \
  kube-prometheus-stack oci://ghcr.io/prometheus-community/charts/kube-prometheus-stack
```

You can now update your release by combining the values from above with the following:

```yaml title="values-kind-recruit-enable-servicemonitors.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-enable-servicemonitors.yaml"
```

<!-- x-release-please-start-version -->

```sh
helm upgrade -n recruit \
  --version 11.1.0 \
  -f https://raw.githubusercontent.com/open-recruit/recruit/refs/tags/v11.1.0/charts/recruit/values-demo.yaml \
  -f values-kind-recruit.yaml \
  -f values-kind-recruit-enable-servicemonitors.yaml \
  --wait \
  recruit oci://ghcr.io/open-recruit/recruit/charts/recruit
```

<!-- x-release-please-end-version -->

`grafanaDashboard.enabled` creates a ConfigMap with recruIT's Grafana dashboard, which the Grafana included with
`kube-prometheus-stack` picks up on its own. To open it, forward Grafana's port:

```sh
kubectl port-forward -n monitoring svc/kube-prometheus-stack-grafana 3000:80
```

Then sign in at <http://127.0.0.1:3000> as `admin`, with the password from
`kubectl get secret -n monitoring kube-prometheus-stack-grafana -o jsonpath='{.data.admin-password}' | base64 -d`,
and open **recruIT** in the dashboard list:

<!-- markdownlint-disable MD033 -->
<figure markdown>
  ![The recruIT dashboard in Grafana](_img/grafana-recruit-dashboard.png)
  <figcaption>The recruIT dashboard in Grafana</figcaption>
</figure>
<!-- markdownlint-enable MD033 -->

The dashboard finds the screening list's and the query module's metrics by their Prometheus `job` label, which the
ServiceMonitors set to the services' names, so it picks the first job containing `list` and `query`. If you scrape
them some other way, pick the right jobs in the **Screening list job** and **Query module job** selectors at the top.

Grafana's dashboard sidecar only looks for ConfigMaps labeled `grafana_dashboard: "1"` by default, and in some setups
only in its own namespace. Use `grafanaDashboard.labels` and `grafanaDashboard.namespace` to match yours. Without the
sidecar, import `charts/recruit/files/dashboards/recruit.json` from the repository in Grafana instead.

## High availability

The screening list and the FHIR server support running multiple replicas, so they stay available if a single pod or
node fails. The screening list keeps all of its state, including its notification job queue, in PostgreSQL, so its
replicas don't need any further setup. The query module should only run as a single replica.

The following values run two replicas of both, create
[PodDisruptionBudgets](https://kubernetes.io/docs/tasks/run-application/configure-pdb/) for them, and use
[pod topology spread constraints](https://kubernetes.io/docs/concepts/scheduling-eviction/topology-spread-constraints/)
to spread their pods across the cluster's topology zones:

```yaml title="values-kind-recruit-ha.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-ha.yaml"
```

The included PostgreSQL databases only run a single instance. For highly available databases, see
[CloudNativePG for HA databases](#cloudnativepg-for-ha-databases).

## CloudNativePG for HA databases

Install the [CloudNativePG](https://cloudnative-pg.io/) operator first:

```sh
helm upgrade --install -n cnpg-system --create-namespace \
  --repo https://cloudnative-pg.github.io/charts \
  --wait \
  cnpg cloudnative-pg
```

Next, create PostgreSQL clusters and users for the HAPI FHIR server and the screening list:

```yaml title="cnpg-clusters.yaml"
--8<-- "_snippets/k8s/cnpg-clusters.yaml"
```

```sh
kubectl apply -n recruit -f cnpg-clusters.yaml
```

Finally, install the recruIT chart with the following values added, which replace the included PostgreSQL databases
with the clusters' read-write services:

```yaml title="values-kind-recruit-with-cnpg.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-with-cnpg.yaml"
```

## Service mesh integration

The application can be integrated with a service mesh, both for observability and to secure service-to-service
communication via mTLS.

### Linkerd

The following values add Linkerd's `linkerd.io/inject: enabled` annotation to the pods of all services. The chart's
Jobs have their own `podAnnotations` and aren't injected, since a sidecar would keep them from completing. The ports
of PostgreSQL and SMTP, whose protocols Linkerd can't detect, are marked as opaque:

```yaml title="values-kind-recruit-linkerd.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-linkerd.yaml"
```

You can also add the `linkerd.io/inject: enabled` annotation to the `recruit` namespace instead, see
[Automatic proxy injection](https://linkerd.io/2-edge/features/proxy-injection/), but then have to disable it for the
chart's Jobs as shown for Istio below.

### Istio

Label the namespace to have Istio inject its Envoy sidecar into every pod deployed to it:

```sh
kubectl label namespace recruit istio-injection=enabled
```

and disable the injection for the chart's Jobs:

```yaml title="values-kind-recruit-istio.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-istio.yaml"
```

## Zero-trust networking

To limit the communication between the components, you can deploy
[Kubernetes NetworkPolicy](https://kubernetes.io/docs/concepts/services-networking/network-policies/) resources.
Because the details of a deployment can differ significantly (external databases, dependencies spread across several
namespaces, etc.), no `NetworkPolicy` resources are included in the Helm chart. Instead, the following policies and
explanations should provide a starting point for customization.

The policies are based on these assumptions:

1. recruIT is deployed as a release named `recruit` in a namespace called `recruit`, with the included HAPI FHIR
   server and PostgreSQL databases
1. Trino is deployed in a namespace called `trino`
1. the SMTP server is running on a host outside the cluster at IP `192.0.2.1` and port `587`
1. the Kubernetes API server is reachable at IP `192.0.2.10` and port `6443`
1. the Prometheus monitoring stack from [Metrics](#metrics) is deployed in a namespace called `monitoring`
1. the ingress controller is Traefik, deployed in a namespace called `traefik`

Everything not explicitly allowed is denied, including the pods `helm test` starts. You can use
<https://editor.networkpolicy.io/> to visualize and edit individual policies.

```yaml title="recruit-network-policies.yaml"
--8<-- "_snippets/k8s/recruit-network-policies.yaml"
```

## Distributed tracing

All modules support distributed tracing based on OpenTelemetry, exporting traces via OTLP.

For testing, you can install an all-in-one [Jaeger](https://www.jaegertracing.io/) instance, which keeps the traces in
memory:

```sh
helm upgrade --install -n observability --create-namespace \
  --repo https://jaegertracing.github.io/helm-charts \
  --wait \
  jaeger jaeger
```

The following values enable tracing for the screening list, the query module, and the HAPI FHIR server:

```yaml title="values-kind-recruit-tracing.yaml"
--8<-- "_snippets/k8s/values-kind-recruit-tracing.yaml"
```

To view the traces, forward Jaeger's port and open <http://127.0.0.1:16686>:

```sh
kubectl port-forward -n observability svc/jaeger 16686:16686
```

## Running the query module using Argo Workflows

By default, the query module runs on its own schedule. It can also run once and exit, setting
`QUERY_SQL_ON_FHIR_RUN_MODE=one-shot`, which is useful for running it right after the jobs that update the data it
queries, e.g. using [Airflow](https://airflow.apache.org/docs/apache-airflow/stable/administration-and-deployment/kubernetes.html)
or [Argo Workflows](https://argoproj.github.io/argo-workflows/). It exits with a non-zero code if any study failed, so
the workflow step fails too. Set `querySqlOnFhir.enabled=false` in the chart so it doesn't also run on its schedule.

Below you can find an example for running the query module as part of a larger workflow. Adjust the FHIR server and
Trino settings to your installation, see [Configuration options](../configuration/options.md):

```yaml title="query-argo-workflow.yaml"
--8<-- "_snippets/k8s/query-argo-workflow.yaml"
```

```sh
argo submit -n recruit --wait --log query-argo-workflow.yaml
```

### Metrics for one-shot runs

A one-shot run is usually over before Prometheus would scrape its metrics, so the example pushes them to a
[Prometheus Pushgateway](https://github.com/prometheus/pushgateway) instead: every minute while running, and once more
right before exiting. Each run replaces what the previous one pushed. To install a Pushgateway that the
`kube-prometheus-stack` from [Metrics](#metrics) scrapes:

```sh
helm upgrade --install -n monitoring prometheus-pushgateway \
  oci://ghcr.io/prometheus-community/charts/prometheus-pushgateway \
  --set serviceMonitor.enabled=true \
  --set serviceMonitor.additionalLabels.release=kube-prometheus-stack
```

Its ServiceMonitor keeps the labels the metrics were pushed with (`honorLabels: true`). If you scrape the Pushgateway
some other way, set `honor_labels: true` there too, or Prometheus replaces the pushed `job` and `namespace` labels
with the Pushgateway's own. The pushed `namespace` label comes from
`MANAGEMENT_PROMETHEUS_METRICS_EXPORT_PUSHGATEWAY_GROUPINGKEY_NAMESPACE` in the example.

The recruIT dashboard's eligibility query panels then work as for the scheduled module. **Last successful poll** at
the top shows how long ago a run last finished without any study failing, which is also a good metric to alert on
(`recruit_query_poll_last_success_timestamp_seconds`): it catches failed runs and runs that never happened. The
dashboard's runtime panels only show a one-shot run's last moments, though, so they don't mean much for it.

The `metrics` section of the example's `recruit-query` step adds Argo's own metrics for the step: how long it took
and whether it succeeded, also for runs that failed before the query module could push anything.
