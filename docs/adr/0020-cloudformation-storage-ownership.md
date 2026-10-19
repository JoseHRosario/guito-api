# CloudFormation ownership of Guito storage

## Status

Accepted follow-up to #156. José approved moving session infrastructure management
from custom AWS provisioning to CloudFormation, plus adoption of the existing
shared Aurora PostgreSQL cluster and writer. He separately approved scoped
CloudFormation deployment permissions and read-only CloudFormation inspection for
MinervaAIAgent, and explicitly approved an import-only shared Postgres adoption
that preserves live settings and data. Source implementation, service validation,
reviewed change sets, executed imports and deployed application acceptance are
separate evidence gates.

This does not authorize a new Postgres cluster, RDS setting changes, password or
secret management, networking changes, financial writes, production application
deployment, resource deletion or modifications under ~/.aws.

## Decision

Use native JSON CloudFormation templates and the existing GitHub Actions workflow.
JSON keeps template parsing and hermetic deployment tests in Python's standard
library; no runtime IaC SDK, additional package manager, custom resource provider
or paid CloudFormation hook is needed. Native AWS resources have no additional
CloudFormation service charge; their normal AWS resource charges still apply.

| Stack | Ownership | Deployment boundary |
|---|---|---|
| `guito-sessions-staging` | `guito-api-user-sessions-staging` DynamoDB table | Staging API lane |
| `guito-sessions-production` | `guito-api-user-sessions` DynamoDB table | Separately approved production API lane |
| `guito-postgres-shared` | Existing `db-cluster` Aurora cluster and `guito` serverless writer | Explicit infrastructure-only adoption; not an automatic database reconfiguration lane |

Staging and production source configuration point to the same physical Aurora
cluster, using separate `guito_staging` and `guito_prod` logical databases. The
cluster's initial DatabaseName is `guito_dev`. A physical resource has one
CloudFormation owner, so it must not appear in two environment stacks. Existing
VPC, subnet group, security group, parameter groups and credential secrets remain
external dependencies, not newly created or implicitly adopted resources.

CloudFormation manages storage resources and settings, not SQL tables, database
roles or financial rows. Existing numbered SQL migrations and `_migrations` remain
pipeline-owned and unchanged. Runtime cookies, Google authorization, session
credentials/encryption, endpoint shapes, financial authority and application
version remain unchanged by this infrastructure follow-up.

## Desired state and cost boundaries

`infra/user-sessions.json` retains Standard provisioned 1 RCU / 1 WCU per table,
`handle_hash` String partition key, numeric `ttl` cleanup, deletion protection,
AWS-owned encryption and disabled PITR. No autoscaling, indexes, replication,
Streams or paid extras are introduced. Its fixed physical names are deliberate:
they match the already-installed runtime configuration and existing table identity.
DeletionPolicy and UpdateReplacePolicy are both Retain.

`infra/postgres-shared.json` records the observed existing configuration for
adoption, not a recommended new-database configuration. The live baseline is
Aurora PostgreSQL 17.9, one `db.serverless` writer, scaling 0..16 ACU with a
300-second auto-pause, Data API enabled, and existing network/parameter/maintenance settings.
It includes no password, managed-password switch or new secret resource. Engine
version and subnet group are cluster-owned; the writer inherits them. Both
resources use Retain policies.

### Existing security posture — not silently changed

Read-only discovery found cluster StorageEncrypted=false,
DeletionProtection=false and writer PubliclyAccessible=true. Public accessibility
is not proof that every source IP is permitted by security groups, but it is an
existing exposure property that needs separate review. José explicitly chose to
preserve current settings during import and keep hardening separate. These values
must never be treated as safe defaults for creating another database.

No workflow may create this RDS stack's physical resources or automatically apply
configuration updates. The deployment role receives no new RDS create, delete or
modify permission. Import is blocked on a live/template mismatch. The shared stack
receives an update-deny policy after adoption; any future reconfiguration needs an
explicit owner decision, reviewed permissions and safety plan.

## Import safety and ownership transfer

CloudFormation import is not an automatic assertion that a template matches AWS.
In particular, AWS documents that DBInstance import can apply mismatched template
configuration. Therefore:

1. Read the actual resource identities and every managed setting before planning.
   Reject unexpected members, pending modifications, different engine/network/
   capacity/security settings or an already incompatible stack owner.
2. Validate the native template and obtain resource identifier properties from
   CloudFormation. Map only the known logical IDs to the existing TableName,
   DBClusterIdentifier and DBInstanceIdentifier.
3. Create an IMPORT change set, not a CREATE/UPDATE fallback for existing resources.
   Verify its exact stack/account/Region, template, parameters and complete action
   list. Only the expected Import actions may appear; no creation, deletion,
   replacement, unrelated resource or unapproved service role is accepted.
4. Before execution, verify the explicitly supplied change-set ARN and re-read the
   live resource baseline. A source change or intervening drift invalidates the
   plan. Do not silently generate and execute another plan.
5. Execute through Actions, wait with a finite bound, and read ownership/settings
   back. Install and verify the shared Postgres update-deny policy before running
   supported drift detection, so a later verification failure does not leave it
   unprotected. Reconciliation after a completed import may finish only the
   approved protection/verification steps; it must not re-execute the consumed
   change set. No data copy, reset, password rotation or SQL cleanup is part of
   ownership transfer.

A missing session stack with an existing table requires this explicit adoption
path; a normal API deploy must not recreate or silently import it. New absent
session tables may be created through CloudFormation during an owner-approved
environment rollout. Missing Postgres resources must always fail; this template is
not a database bootstrap mechanism.

## CI/CD integration

Keep the existing registered `ci-cd.yml`; do not depend on a new feature-only
workflow that GitHub cannot dispatch before registration on the default branch.
Infrastructure-only dispatch selects a target and either plans an import or
executes a specific reviewed change-set ARN. Shared Postgres and production
session adoption use the existing `Production` GitHub environment, but do not run
production application deployment. Read-only repository inspection found no
required-reviewer or deployment-branch rules on that environment; its name alone
is not an enforced approval gate. Explicit owner approval and infrastructure-only
workflow dispatch remain required. Ordinary app deployment jobs are explicitly
excluded from infrastructure-only dispatch.

Serialize storage/application operations in a single API workflow concurrency
lane, with cancellation disabled, because they share database ownership and IAM
roles. Bind workflow inputs through environment variables and shell argument
arrays rather than interpolating arbitrary expressions into commands. Publish
only nonsecret plan/status artifacts, never credentials, item contents or financial
source data.

Normal application deployment verifies the shared database stack, ensures the
selected environment's session stack, then runs the existing SQL migrations,
code deployment and read-only smoke tests. The old DynamoDB helper becomes a
verifier; it must no longer issue CreateTable or UpdateTimeToLive. CloudFormation
is the only provisioning owner. Its template/stack outputs and service readback
must match the environment's runtime table and Region before code replacement.

## Permission model

Keep CloudFormation permissions on the existing GitHub deployment role limited
to the named storage stacks and named change-set prefix. Resource-type and
underlying service permissions must prevent unrelated provisioning and new RDS
creation/reconfiguration. RDS metadata/tag bookkeeping permission is distinct
from SQL data access or service configuration changes. Native instance-provider
network describes and CloudFormation BatchDescribeTypeConfigurations require
Region-bounded Resource=* where IAM offers no useful resource-level granularity.
The latter is required for drift detection; the controller does not directly read
unrelated extension configuration. Drift permission closure includes
DetectStackDrift, DetectStackResourceDrift, BatchDescribeTypeConfigurations and
underlying native resource reads, not just the first API call. Named-stack tag
bookkeeping and named stack/change-set event inspection remain separately scoped.
Preserve all existing
policies and grants; do not attach broad CloudFormation or RDS administrator roles.
MinervaAIAgent receives read-only stack/plan/template inspection, not authority to
execute stack changes or new DynamoDB data access.

Retention protects data from stack removal but leaves an orphaned resource; it is
not an automatic rollback or permission to delete a stack. Do not remove resources
from templates, downgrade retention, recreate resources or transfer their ownership
again merely to make a failed deployment pass. After an ambiguous import, inspect
actual stack ownership and native resource state before recovery.

## Verification and outstanding gates

Hermetic public-CLI tests exercise native service fakes, import previews, exact-ARN
execution, account/Region/target isolation, ownership idempotence, unexpected
changes, template/parameter drift, stale plans and no competing provisioner. They
cannot establish real CloudFormation/RDS import behavior. The live gates are native
service validation, review of exact import change sets, execution/readback,
unchanged database settings/identities and successful staging CI. Existing Google/
Android acceptance remains independent of this infrastructure work.

Sources: [CloudFormation import](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/import-resources-manually.html),
[RDS DBInstance import warning](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-rds-dbinstance.html),
[CloudFormation pricing](https://aws.amazon.com/cloudformation/pricing/).
