# Storage infrastructure operations

[ADR-0020](adr/0020-cloudformation-storage-ownership.md) records ownership, approvals,
retention, cost and import safety. CloudFormation ownership is separate from
application version, SQL schema version and live acceptance. A checked-in template
is not evidence that a stack was imported or deployed.

## Current owner decision and Aurora status

José selected **Finish DynamoDB; pause Aurora adoption**, superseding only the
earlier Aurora import approval. Aurora is **TEMPLATE ONLY — not adopted and not
deployable**. Supplied operator evidence: native import attempted
`rds:ModifyDBCluster` despite full baseline parity; the deployment role denied it.
`guito-postgres-shared` is empty `ROLLBACK_COMPLETE`, owns no Aurora resources
(native resource count **0**), and before/after DB identities/settings are identical.
Do not retry import, grant RDS Modify or delete the failed empty stack: deletion
is not approved. Keep `infra/postgres-shared.json` prepared for a separate future
decision, not an executable adoption lane.

The controller rejects Postgres `plan-import`, `execute-import` (including recovery)
and `ensure` before any AWS call, with no bypass flag. Read-only `verify` remains
available diagnostically; an empty failed stack cannot pass managed verification.

## Inventory

| Target | CloudFormation stack | Template |
|---|---|---|
| `sessions-staging` | `guito-sessions-staging` | `infra/user-sessions.json`, Environment=staging |
| `sessions-production` | `guito-sessions-production` | `infra/user-sessions.json`, Environment=production |
| `postgres-shared` | `guito-postgres-shared` | `infra/postgres-shared.json`, no environment parameter |

All targets are in Ireland (`eu-west-1`), account `497087877832`. Session tables
remain `guito-api-user-sessions-staging` and `guito-api-user-sessions`. The shared
Postgres resources remain cluster `db-cluster` and writer `guito`. Staging and
production use separate logical databases in that one cluster. Do not create a
second cluster, rename resources or try to import the same cluster into two stacks.
Existing secrets, VPC, subnet/security groups and parameter groups are not adopted.
No template contains a password or enables RDS-managed password rotation.
Both adoption templates permanently omit optional `Outputs`: native IMPORT rejects
adding Outputs even when they are valid for CREATE. No exports or output consumers
exist, so there is no separate output-update phase. The controller rejects nonempty
Outputs before planning or executing an import; ownership is checked through
`ListStackResources` and native service readback against fixed runtime identities.

## Normal GitHub Actions deployments

The existing workflow tests source before its deployment jobs. A normal app
rollout ensures the selected session stack,
checks native service settings, then continues the existing SQL migrations and
Lambda/edge/smoke sequence. The old DynamoDB helper only verifies; it cannot create
a table or enable TTL anymore. There is one infrastructure provisioning owner.

- An unchanged managed session stack is verified, not recreated.
- An absent session table may be created through CloudFormation in an approved
  environment deployment; an already-existing unmanaged table blocks deployment
  until explicitly imported.
- Unmanaged Aurora or the empty failed shared stack does not gate normal rollout.
  Existing financial SQL migrations remain unchanged; Aurora adoption stays paused.
- Unexpected session infrastructure drift, failed session import or incompatible ownership blocks
  application rollout. Do not bypass that failure or point staging at production.
- Production app rollout retains its separate review/approval boundary. An
  infrastructure-only dispatch must not invoke either app deployment job.

## One-time adoption through the existing registered workflow

Use an exact reviewed feature ref. These commands dispatch the existing
`ci-cd.yml`; they do not run deployment locally. A new feature-only workflow file
would not yet be registered for dispatch on the repository default branch.

Plan a staging session-table import:

```bash
gh workflow run ci-cd.yml --repo JoseHRosario/guito-api \
  --ref feature/156-persistent-login \
  -f infrastructure_target=sessions-staging \
  -f infrastructure_action=plan-import
```

Only `sessions-staging` and `sessions-production` are supported adoption targets.
The production session target uses the existing `Production` GitHub environment;
its name is not an enforced approval gate (no required-reviewer/branch rules were
found). Explicit approval remains required. Postgres is absent from choices and
rejected by input validation before OIDC. Every infrastructure-only dispatch,
including an invalid Postgres request, skips both application jobs.

Read the exact run's `infrastructure-plan.json` artifact and verify:

1. Correct target, account, Region, stack, current source/template and expected
   parameters.
2. IMPORT only: UserSessions for the selected session stack. No Add, Remove,
   replacement or unrelated resources.
3. Resource identifiers still name the existing table/cluster/writer. Fresh native
   service settings match every managed property; no pending modifications or
   intervening drift.
4. No unexpected service-role binding, new secret, network object, paid extra or
   RDS configuration operation.

Execute only the full change-set ARN from that verified artifact:

```bash
gh workflow run ci-cd.yml --repo JoseHRosario/guito-api \
  --ref feature/156-persistent-login \
  -f infrastructure_target=sessions-staging \
  -f infrastructure_action=execute-import \
  -f infrastructure_change_set_arn='<exact reviewed change-set ARN>'
```

The helper checks the fetched change set against the current checked-out template,
parameters and live baseline again. Do not substitute an ARN from another target
or source revision, or create a new plan inside an execute request. No local
`deploy.sh` fallback, implicit adoption or CREATE/UPDATE fallback is permitted for
existing resources. A failed/ambiguous operation requires reading actual stack
and resource state before recovery; termination of a command is not rollback.

After session adoption, read back exact ownership, unchanged native settings and
supported drift status before rerunning a blocked staging deployment. Do not
remove, replace or re-import physical resources merely to make CI green.

### Completing a successful session import after a verification failure

Keep the original reviewed change-set ARN and source. Repeat `execute-import`
through the same session target only. A consumed ARN requires EXECUTE_COMPLETE,
IMPORT_COMPLETE, exact StackId, all-Import physical identities, unchanged
parameters/template, owned resources and fresh native settings. It never calls
ExecuteChangeSet again; only verification/drift checks complete. Missing or changed
evidence fails closed. This is **not** an Aurora retry/recovery instruction.

## IAM and verification

The original fixed-name DynamoDB management policy remains distinct from the
runtime four-action item policy. Additional CloudFormation permissions are
versioned separately in the deployment and inspection policy files. The new
deployment policy has no RDS/EC2 actions or SetStackPolicy grant. CreateChangeSet,
ExecuteChangeSet and stack TagResource writes name only the two session stacks;
DynamoDB metadata/tag permissions name only the two session tables. Required
CloudFormation drift APIs and Region-bounded global permissions remain. Preserve
unrelated policies on both roles. MinervaAIAgent's added access is inspection, not
stack execution or new DynamoDB item access. Credential renewal remains José's
manual action; never write config or caches under ~/.aws.

CloudFormation manages resource lifecycle, while `deploy/db-apply.sh` continues
SQL migrations. Import does not move sessions, change rows, rotate credentials,
run financial operations or establish real Google/Android acceptance. Session
resource verification uses metadata only; never inspect item values for this task.

## Existing Postgres risks and hardening boundary

The prepared, unadopted template records observed unencrypted cluster
storage, disabled RDS deletion protection and writer public accessibility. These
are existing settings, not recommended creation defaults. José approved preserving
them during adoption and keeping hardening separate. Public accessibility alone
does not establish security-group reachability from every IP; that requires its
own review. No workflow may create another database from this adoption-only
template, and no new RDS create/modify/delete authority is granted here.

Both resource retention policies are Retain. Retention prevents an import/deletion
from being interpreted as permission to destroy data; it does not provide a backup
or automatically restore stack ownership. Any future database hardening, version,
capacity, maintenance, networking, secret or destruction change needs a separate
owner-approved plan and appropriate reviewed permissions.
