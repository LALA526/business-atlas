-- T1.0 PostgreSQL 17 reference migration. NOT tested against a running PG instance in this packaging session.
-- Apply in a disposable integration DB using the migration owner; EF mapping/migrations are the single production chain.
-- Every new schema change requires reviewed migration, transaction tests, RLS tests and upgrade rehearsal.
BEGIN;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE SCHEMA IF NOT EXISTS identity;
CREATE SCHEMA IF NOT EXISTS access;
CREATE SCHEMA IF NOT EXISTS core;
CREATE SCHEMA IF NOT EXISTS gov;
CREATE SCHEMA IF NOT EXISTS intake;
CREATE SCHEMA IF NOT EXISTS collab;
CREATE SCHEMA IF NOT EXISTS delivery;
CREATE SCHEMA IF NOT EXISTS assistant;
CREATE SCHEMA IF NOT EXISTS agent;
CREATE SCHEMA IF NOT EXISTS ops;
CREATE SCHEMA IF NOT EXISTS read_model;
CREATE TABLE identity.principal (
 principal_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), issuer text NOT NULL, subject text NOT NULL,
 kind text NOT NULL CHECK (kind IN ('human','agent','service')), active boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(issuer,subject)
);
CREATE TABLE identity.project (
 project_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), slug text NOT NULL UNIQUE, display_name text NOT NULL,
 owner_id uuid NOT NULL REFERENCES identity.principal(principal_id), version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 policy_epoch bigint NOT NULL DEFAULT 1 CHECK(policy_epoch>0), current_baseline_id uuid,
 created_at timestamptz NOT NULL DEFAULT now(), archived_at timestamptz
);
CREATE TABLE identity.membership (
 project_id uuid NOT NULL REFERENCES identity.project(project_id), principal_id uuid NOT NULL REFERENCES identity.principal(principal_id),
 roles text[] NOT NULL, clearance int NOT NULL DEFAULT 0 CHECK(clearance>=0), active boolean NOT NULL DEFAULT true,
 version bigint NOT NULL DEFAULT 1 CHECK(version>0), PRIMARY KEY(project_id,principal_id)
);
CREATE TABLE core.unit (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 unit_id uuid NOT NULL DEFAULT gen_random_uuid(),
 kind text NOT NULL, owner_id uuid NOT NULL REFERENCES identity.principal(principal_id),
 visibility int NOT NULL DEFAULT 0 CHECK(visibility>=0), archived_at timestamptz, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,unit_id)
);
CREATE TABLE access.grant (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 grant_id uuid NOT NULL DEFAULT gen_random_uuid(),
 unit_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES identity.principal(principal_id),
 action text NOT NULL, field_paths text[] NOT NULL DEFAULT ARRAY[]::text[], effect text NOT NULL CHECK(effect IN ('allow','deny')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,grant_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 UNIQUE(project_id,unit_id,principal_id,action,effect)
);
CREATE TABLE core.revision (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 revision_id uuid NOT NULL DEFAULT gen_random_uuid(),
 unit_id uuid NOT NULL, rev_no bigint NOT NULL CHECK(rev_no>0), parent_revision_id uuid,
 schema_type text NOT NULL, schema_version text NOT NULL, schema_definition_revision_id uuid,
 fact_kind text NOT NULL CHECK(fact_kind IN ('normative','observed','documented_intent','design_decision','inferred','assumption')),
 verification text NOT NULL CHECK(verification IN ('unverified','verified','contradicted','insufficient','stale')),
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'), applicability jsonb NOT NULL DEFAULT '{}'::jsonb,
 content_sha256 text NOT NULL CHECK(content_sha256 ~ '^[0-9a-f]{64}$'), sealed boolean NOT NULL DEFAULT false, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,revision_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 UNIQUE(project_id,unit_id,rev_no),
 UNIQUE(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,unit_id,parent_revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,schema_definition_revision_id) REFERENCES core.revision(project_id,revision_id)
);
CREATE TABLE core.revision_ref (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 ref_id uuid NOT NULL DEFAULT gen_random_uuid(),
 source_revision_id uuid NOT NULL, ref_key text NOT NULL, ref_kind text NOT NULL,
 target_unit_id uuid NOT NULL, target_revision_id uuid, required boolean NOT NULL DEFAULT true,
 PRIMARY KEY(project_id,ref_id),
 FOREIGN KEY(project_id,source_revision_id) REFERENCES core.revision(project_id,revision_id),
 FOREIGN KEY(project_id,target_unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,target_unit_id,target_revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 UNIQUE(project_id,source_revision_id,ref_key)
);
CREATE TABLE core.relation_index (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 relation_revision_id uuid NOT NULL DEFAULT gen_random_uuid(),
 predicate text NOT NULL, from_unit_id uuid NOT NULL, to_unit_id uuid NOT NULL,
 PRIMARY KEY(project_id,relation_revision_id),
 FOREIGN KEY(project_id,relation_revision_id) REFERENCES core.revision(project_id,revision_id),
 FOREIGN KEY(project_id,from_unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,to_unit_id) REFERENCES core.unit(project_id,unit_id)
);
CREATE TABLE core.evidence_link (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 evidence_id uuid NOT NULL DEFAULT gen_random_uuid(),
 fact_revision_id uuid NOT NULL, source_revision_id uuid NOT NULL, locator jsonb NOT NULL,
 support text NOT NULL CHECK(support IN ('supports','refutes','contextual')), capture_kind text NOT NULL CHECK(capture_kind IN ('rawCaptured','verifiedScript','reconstructed','proposed','humanAttestation')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,evidence_id),
 FOREIGN KEY(project_id,fact_revision_id) REFERENCES core.revision(project_id,revision_id),
 FOREIGN KEY(project_id,source_revision_id) REFERENCES core.revision(project_id,revision_id)
);
CREATE TABLE core.external_identity (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 mapping_id uuid NOT NULL DEFAULT gen_random_uuid(),
 source_namespace text NOT NULL, external_type text NOT NULL, external_id text NOT NULL, unit_id uuid NOT NULL,
 PRIMARY KEY(project_id,mapping_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 UNIQUE(project_id,source_namespace,external_type,external_id)
);
CREATE TABLE gov.change_set (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 change_set_id uuid NOT NULL DEFAULT gen_random_uuid(),
 base_baseline_id uuid NOT NULL, title text NOT NULL,
 state text NOT NULL CHECK(state IN ('draft','submitted','in_review','changes_requested','approved','rejected','published','withdrawn','conflict')),
 submitted_seq bigint, submit_digest text CHECK(submit_digest IS NULL OR submit_digest ~ '^[0-9a-f]{64}$'),
 review_requirements jsonb NOT NULL DEFAULT '[]'::jsonb, summary jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,change_set_id)
);
CREATE TABLE gov.change_item (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 item_id uuid NOT NULL DEFAULT gen_random_uuid(),
 change_set_id uuid NOT NULL, unit_id uuid NOT NULL, base_revision_id uuid, proposed_revision_id uuid,
 action text NOT NULL CHECK(action IN ('add','revise','retire','preserve','revalidate')), dependency_group text NOT NULL, reason text NOT NULL,
 PRIMARY KEY(project_id,item_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,unit_id,base_revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,unit_id,proposed_revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 UNIQUE(project_id,change_set_id,unit_id),
 CHECK ((action IN ('add','revise') AND proposed_revision_id IS NOT NULL) OR (action IN ('retire','preserve','revalidate') AND base_revision_id IS NOT NULL))
);
CREATE TABLE gov.review_decision (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 decision_id uuid NOT NULL DEFAULT gen_random_uuid(),
 change_set_id uuid NOT NULL, submitted_seq bigint NOT NULL, submit_digest text NOT NULL CHECK(submit_digest ~ '^[0-9a-f]{64}$'),
 approval_role text NOT NULL, scope jsonb NOT NULL, decision text NOT NULL CHECK(decision IN ('approve','reject','request_changes')),
 reason text NOT NULL, anchor jsonb NOT NULL DEFAULT '{}'::jsonb, actor_id uuid NOT NULL REFERENCES identity.principal(principal_id), decided_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,decision_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id)
);
CREATE TABLE gov.baseline (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 baseline_id uuid NOT NULL DEFAULT gen_random_uuid(),
 baseline_no bigint NOT NULL CHECK(baseline_no>=0), parent_baseline_id uuid, change_set_id uuid,
 registry_version text NOT NULL, digest text NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'), sealed boolean NOT NULL DEFAULT false, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,baseline_id),
 FOREIGN KEY(project_id,parent_baseline_id) REFERENCES gov.baseline(project_id,baseline_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 UNIQUE(project_id,baseline_no),
 UNIQUE(project_id,change_set_id)
);
CREATE TABLE gov.baseline_entry (
 project_id uuid NOT NULL REFERENCES identity.project(project_id), baseline_id uuid NOT NULL, unit_id uuid NOT NULL, revision_id uuid NOT NULL,
 PRIMARY KEY(project_id,baseline_id,unit_id),
 UNIQUE(project_id,baseline_id,unit_id,revision_id),
 FOREIGN KEY(project_id,baseline_id) REFERENCES gov.baseline(project_id,baseline_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id)
);
ALTER TABLE gov.change_set ADD CONSTRAINT fk_cs_base FOREIGN KEY(project_id,base_baseline_id) REFERENCES gov.baseline(project_id,baseline_id);
ALTER TABLE identity.project ADD CONSTRAINT fk_current_baseline FOREIGN KEY(project_id,current_baseline_id) REFERENCES gov.baseline(project_id,baseline_id);
CREATE TABLE intake.blob (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 blob_id uuid NOT NULL DEFAULT gen_random_uuid(),
 sha256 text NOT NULL CHECK(sha256 ~ '^[0-9a-f]{64}$'), byte_size bigint NOT NULL CHECK(byte_size>=0), storage_key text NOT NULL,
 scan_status text NOT NULL CHECK(scan_status IN ('pending','clean','rejected','unavailable')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,blob_id),
 UNIQUE(project_id,sha256), UNIQUE(project_id,storage_key)
);
CREATE TABLE intake.source_blob_link (
 project_id uuid NOT NULL REFERENCES identity.project(project_id), source_revision_id uuid NOT NULL, blob_id uuid NOT NULL,
 PRIMARY KEY(project_id,source_revision_id,blob_id),
 FOREIGN KEY(project_id,source_revision_id) REFERENCES core.revision(project_id,revision_id),
 FOREIGN KEY(project_id,blob_id) REFERENCES intake.blob(project_id,blob_id)
);
CREATE TABLE intake.upload (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 upload_id uuid NOT NULL DEFAULT gen_random_uuid(),
 blob_id uuid, original_name text NOT NULL, content_type text NOT NULL, expires_at timestamptz NOT NULL,
 state text NOT NULL CHECK(state IN ('staged','scanning','ready','rejected','expired')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,upload_id),
 FOREIGN KEY(project_id,blob_id) REFERENCES intake.blob(project_id,blob_id)
);
CREATE TABLE intake.import_batch (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 import_id uuid NOT NULL DEFAULT gen_random_uuid(),
 package_id text NOT NULL, format_version text NOT NULL, package_sha256 text NOT NULL CHECK(package_sha256 ~ '^[0-9a-f]{64}$'),
 source_namespace text NOT NULL, blob_id uuid NOT NULL, base_baseline_id uuid NOT NULL, change_set_id uuid,
 state text NOT NULL CHECK(state IN ('uploaded','validating','preview_ready','decided','staged','discarded','failed')), summary jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,import_id),
 FOREIGN KEY(project_id,blob_id) REFERENCES intake.blob(project_id,blob_id),
 FOREIGN KEY(project_id,base_baseline_id) REFERENCES gov.baseline(project_id,baseline_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 UNIQUE(project_id,source_namespace,package_id),
 UNIQUE(project_id,source_namespace,package_sha256)
);
CREATE TABLE intake.import_entry (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 entry_id uuid NOT NULL DEFAULT gen_random_uuid(),
 import_id uuid NOT NULL, external_type text NOT NULL, external_id text NOT NULL, unit_id uuid,
 diff jsonb NOT NULL DEFAULT '{}'::jsonb, decision text CHECK(decision IN ('reuse_existing','new_version','new_identity','reject','needs_evidence')),
 reason text, decided_by uuid REFERENCES identity.principal(principal_id),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,entry_id),
 FOREIGN KEY(project_id,import_id) REFERENCES intake.import_batch(project_id,import_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 UNIQUE(project_id,import_id,external_type,external_id)
);
CREATE TABLE collab.task (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 task_id uuid NOT NULL DEFAULT gen_random_uuid(),
 root_unit_id uuid, base_baseline_id uuid NOT NULL, intent text NOT NULL CHECK(length(trim(intent))>0),
 title text NOT NULL, classification text NOT NULL CHECK(classification IN ('unknown','business_change','implementation_only','knowledge_only')),
 state text NOT NULL CHECK(state IN ('captured','investigating','needs_decision','ready','implementing','verifying','awaiting_knowledge','ready_for_acceptance','completed','blocked','cancelled')),
 external_ref jsonb NOT NULL DEFAULT '{}'::jsonb, target_summary jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,task_id),
 FOREIGN KEY(project_id,root_unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,base_baseline_id) REFERENCES gov.baseline(project_id,baseline_id)
);
CREATE TABLE collab.issue (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 issue_id uuid NOT NULL DEFAULT gen_random_uuid(),
 unit_id uuid, revision_id uuid, task_id uuid, change_set_id uuid, anchor jsonb NOT NULL DEFAULT '{}'::jsonb,
 body text NOT NULL CHECK(length(trim(body))>0), state text NOT NULL CHECK(state IN ('open','addressed','closed','withdrawn')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,issue_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 CHECK (unit_id IS NOT NULL OR task_id IS NOT NULL OR change_set_id IS NOT NULL),
 CHECK (revision_id IS NULL OR unit_id IS NOT NULL)
);
CREATE TABLE collab.feedback (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 feedback_id uuid NOT NULL DEFAULT gen_random_uuid(),
 unit_id uuid, revision_id uuid, task_id uuid, change_set_id uuid, anchor jsonb NOT NULL DEFAULT '{}'::jsonb,
 body text NOT NULL CHECK(length(trim(body))>0), state text NOT NULL CHECK(state IN ('open','addressed','closed','withdrawn')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,feedback_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 CHECK (unit_id IS NOT NULL OR task_id IS NOT NULL OR change_set_id IS NOT NULL),
 CHECK (revision_id IS NULL OR unit_id IS NOT NULL)
);
CREATE TABLE collab.comment (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 comment_id uuid NOT NULL DEFAULT gen_random_uuid(),
 unit_id uuid, revision_id uuid, task_id uuid, change_set_id uuid, anchor jsonb NOT NULL DEFAULT '{}'::jsonb,
 body text NOT NULL CHECK(length(trim(body))>0), state text NOT NULL CHECK(state IN ('open','addressed','closed','withdrawn')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,comment_id),
 FOREIGN KEY(project_id,unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 CHECK (unit_id IS NOT NULL OR task_id IS NOT NULL OR change_set_id IS NOT NULL),
 CHECK (revision_id IS NULL OR unit_id IS NOT NULL)
);
CREATE TABLE collab.investigation (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 investigation_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, base_baseline_id uuid NOT NULL, scope jsonb NOT NULL,
 evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb, coverage jsonb NOT NULL DEFAULT '{}'::jsonb, capture_kind text NOT NULL CHECK(capture_kind IN ('rawCaptured','verifiedScript','reconstructed','proposed','humanAttestation')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,investigation_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,base_baseline_id) REFERENCES gov.baseline(project_id,baseline_id)
);
CREATE TABLE collab.impact (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 impact_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, target_unit_id uuid, target_revision_id uuid, locator jsonb NOT NULL DEFAULT '{}'::jsonb, path jsonb NOT NULL DEFAULT '[]'::jsonb,
 disposition text NOT NULL CHECK(disposition IN ('required','potential','no_change','deferred','unknown')), inspection_state text NOT NULL CHECK(inspection_state IN ('candidate','inspected','verified','stale')),
 investigation_id uuid, reason text NOT NULL, risk text NOT NULL CHECK(risk IN ('low','medium','high','critical')), assigned_task_id uuid, evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb,
 close_state text NOT NULL CHECK(close_state IN ('open','resolved','deferred')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,impact_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,target_unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,target_unit_id,target_revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 FOREIGN KEY(project_id,investigation_id) REFERENCES collab.investigation(project_id,investigation_id),
 FOREIGN KEY(project_id,assigned_task_id) REFERENCES collab.task(project_id,task_id),
 CHECK (target_revision_id IS NULL OR target_unit_id IS NOT NULL),
 CHECK (target_unit_id IS NOT NULL OR locator <> '{}'::jsonb),
 CHECK (disposition <> 'no_change' OR investigation_id IS NOT NULL),
 CHECK (disposition <> 'deferred' OR assigned_task_id IS NOT NULL)
);
CREATE TABLE delivery.task_context (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 context_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, baseline_id uuid NOT NULL, policy_epoch bigint NOT NULL, phase text NOT NULL CHECK(phase IN ('candidate','executable')),
 contract_digest text NOT NULL CHECK(contract_digest ~ '^[0-9a-f]{64}$'), body jsonb NOT NULL, expires_at timestamptz, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,context_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,baseline_id) REFERENCES gov.baseline(project_id,baseline_id)
);
CREATE TABLE delivery.context_dependency (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 dependency_id uuid NOT NULL DEFAULT gen_random_uuid(),
 context_id uuid NOT NULL, unit_id uuid NOT NULL, revision_id uuid NOT NULL, required boolean NOT NULL, purpose text NOT NULL,
 PRIMARY KEY(project_id,dependency_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id),
 UNIQUE(project_id,context_id,unit_id,revision_id,purpose)
);
CREATE TABLE delivery.context_invalidation (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 invalidation_id uuid NOT NULL DEFAULT gen_random_uuid(),
 context_id uuid NOT NULL, cause text NOT NULL, event_id uuid, invalidated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,invalidation_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id)
);
CREATE TABLE delivery.conformance (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 check_id uuid NOT NULL DEFAULT gen_random_uuid(),
 normative_revision_id uuid NOT NULL, observed_revision_id uuid NOT NULL, build_digest text NOT NULL,
 environment text NOT NULL, context_sha256 text NOT NULL CHECK(context_sha256 ~ '^[0-9a-f]{64}$'), evidence_sha256 text NOT NULL CHECK(evidence_sha256 ~ '^[0-9a-f]{64}$'),
 evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb, expected jsonb NOT NULL DEFAULT '{}'::jsonb, actual jsonb NOT NULL DEFAULT '{}'::jsonb, verdict text NOT NULL CHECK(verdict IN ('conformant','divergent','insufficient','unverified','stale','conflict')),
 method text NOT NULL, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,check_id),
 FOREIGN KEY(project_id,normative_revision_id) REFERENCES core.revision(project_id,revision_id),
 FOREIGN KEY(project_id,observed_revision_id) REFERENCES core.revision(project_id,revision_id)
);
CREATE TABLE delivery.acceptance (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 acceptance_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, context_id uuid NOT NULL, test_id text NOT NULL, expected jsonb NOT NULL, observed jsonb,
 result text NOT NULL CHECK(result IN ('pass','fail','not_run','unknown')), build_digest text, environment text, evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,acceptance_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id),
 CHECK(result <> 'pass' OR (observed IS NOT NULL AND build_digest IS NOT NULL AND jsonb_array_length(evidence_refs)>0))
);
CREATE TABLE delivery.proposal_intake (
 project_id uuid NOT NULL REFERENCES identity.project(project_id), proposal_sha256 text NOT NULL CHECK(proposal_sha256 ~ '^[0-9a-f]{64}$'),
 proposal_external_id text NOT NULL, schema_version text NOT NULL, task_id uuid NOT NULL, repo_key text NOT NULL, blob_id uuid NOT NULL,
 created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,proposal_sha256),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,blob_id) REFERENCES intake.blob(project_id,blob_id),
 UNIQUE(project_id,repo_key,proposal_external_id)
);
CREATE TABLE delivery.delivery_record (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 delivery_id uuid NOT NULL DEFAULT gen_random_uuid(),
 external_delivery_id text NOT NULL, task_id uuid NOT NULL, repo_key text NOT NULL,
 final_commit_sha text NOT NULL CHECK(final_commit_sha ~ '^[0-9a-f]{40,64}$'), manifest_sha256 text NOT NULL CHECK(manifest_sha256 ~ '^[0-9a-f]{64}$'),
 build_digest text NOT NULL, proposal_sha256 text NOT NULL CHECK(proposal_sha256 ~ '^[0-9a-f]{64}$'), blob_id uuid NOT NULL, change_set_id uuid, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,delivery_id),
 FOREIGN KEY(project_id,proposal_sha256) REFERENCES delivery.proposal_intake(project_id,proposal_sha256),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,blob_id) REFERENCES intake.blob(project_id,blob_id),
 FOREIGN KEY(project_id,change_set_id) REFERENCES gov.change_set(project_id,change_set_id),
 UNIQUE(project_id,repo_key,external_delivery_id)
);
CREATE TABLE delivery.deployment_record (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 deployment_id uuid NOT NULL DEFAULT gen_random_uuid(),
 environment text NOT NULL, repo_key text NOT NULL, final_commit_sha text NOT NULL,
 build_digest text NOT NULL, migration_version text NOT NULL, config_digest text NOT NULL, previous_deployment_id uuid,
 deployed_at timestamptz NOT NULL, evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,deployment_id),
 FOREIGN KEY(project_id,previous_deployment_id) REFERENCES delivery.deployment_record(project_id,deployment_id)
);
CREATE TABLE delivery.knowledge_debt (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 debt_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, fix_task_id uuid, owner_id uuid NOT NULL REFERENCES identity.principal(principal_id), due_at timestamptz NOT NULL,
 approval_ref text NOT NULL, risk text NOT NULL, reason text NOT NULL, state text NOT NULL CHECK(state IN ('open','resolved','overdue')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,debt_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,fix_task_id) REFERENCES collab.task(project_id,task_id)
);
CREATE TABLE agent.run (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 run_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid, context_id uuid, executor_id uuid NOT NULL REFERENCES identity.principal(principal_id),
 state text NOT NULL CHECK(state IN ('queued','running','waiting_external','succeeded','failed','canceled','stale')), provider text, model text,
 checkpoint jsonb NOT NULL DEFAULT '{}'::jsonb, budget jsonb NOT NULL DEFAULT '{}'::jsonb, usage jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,run_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id)
);
CREATE TABLE agent.run_event (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 event_id uuid NOT NULL DEFAULT gen_random_uuid(),
 run_id uuid NOT NULL, seq bigint NOT NULL CHECK(seq>=0), external_event_id text NOT NULL,
 capture_kind text NOT NULL CHECK(capture_kind IN ('rawCaptured','verifiedScript','reconstructed','proposed','humanAttestation')), event_type text NOT NULL,
 event_data jsonb NOT NULL, artifact_sha256 text, occurred_at timestamptz NOT NULL, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,event_id),
 FOREIGN KEY(project_id,run_id) REFERENCES agent.run(project_id,run_id),
 UNIQUE(project_id,run_id,seq),
 UNIQUE(project_id,run_id,external_event_id)
);
CREATE TABLE agent.work_request (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 work_id uuid NOT NULL DEFAULT gen_random_uuid(),
 task_id uuid NOT NULL, context_id uuid NOT NULL, run_id uuid, kind text NOT NULL CHECK(kind IN ('investigate','review','implement','verify')),
 scope jsonb NOT NULL, budget jsonb NOT NULL DEFAULT '{}'::jsonb, deadline timestamptz, state text NOT NULL CHECK(state IN ('queued','leased','running','waiting_external','succeeded','failed','canceled','blocked')),
 holder_id uuid REFERENCES identity.principal(principal_id), lease_until timestamptz, fence bigint NOT NULL DEFAULT 0,
 attempt int NOT NULL DEFAULT 0, checkpoint jsonb NOT NULL DEFAULT '{}'::jsonb, result_ref jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,work_id),
 FOREIGN KEY(project_id,task_id) REFERENCES collab.task(project_id,task_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id),
 FOREIGN KEY(project_id,run_id) REFERENCES agent.run(project_id,run_id)
);
CREATE TABLE assistant.provider (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 provider_id uuid NOT NULL DEFAULT gen_random_uuid(),
 provider_key text NOT NULL, endpoint text NOT NULL, model text NOT NULL, secret_ref text NOT NULL,
 allowed boolean NOT NULL DEFAULT false, options jsonb NOT NULL DEFAULT '{}'::jsonb, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,provider_id),
 UNIQUE(project_id,provider_key)
);
CREATE TABLE assistant.conversation (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 conversation_id uuid NOT NULL DEFAULT gen_random_uuid(),
 root_unit_id uuid, baseline_id uuid NOT NULL, title text NOT NULL, owner_id uuid NOT NULL REFERENCES identity.principal(principal_id), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,conversation_id),
 FOREIGN KEY(project_id,root_unit_id) REFERENCES core.unit(project_id,unit_id),
 FOREIGN KEY(project_id,baseline_id) REFERENCES gov.baseline(project_id,baseline_id)
);
CREATE TABLE assistant.message (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 message_id uuid NOT NULL DEFAULT gen_random_uuid(),
 conversation_id uuid NOT NULL, run_id uuid, seq bigint NOT NULL CHECK(seq>=0),
 kind text NOT NULL CHECK(kind IN ('user','assistant','system_note')), body jsonb NOT NULL, created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,message_id),
 FOREIGN KEY(project_id,conversation_id) REFERENCES assistant.conversation(project_id,conversation_id),
 FOREIGN KEY(project_id,run_id) REFERENCES agent.run(project_id,run_id),
 UNIQUE(project_id,conversation_id,seq)
);
CREATE TABLE assistant.citation (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 citation_id uuid NOT NULL DEFAULT gen_random_uuid(),
 message_id uuid NOT NULL, revision_id uuid NOT NULL, locator jsonb NOT NULL, block_id text NOT NULL,
 PRIMARY KEY(project_id,citation_id),
 FOREIGN KEY(project_id,message_id) REFERENCES assistant.message(project_id,message_id),
 FOREIGN KEY(project_id,revision_id) REFERENCES core.revision(project_id,revision_id)
);
CREATE TABLE ops.job (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 job_id uuid NOT NULL DEFAULT gen_random_uuid(),
 kind text NOT NULL, requester_id uuid NOT NULL REFERENCES identity.principal(principal_id), context_id uuid,
 state text NOT NULL CHECK(state IN ('queued','running','waiting_external','succeeded','failed','canceled','cancel_requested')),
 input jsonb NOT NULL, result jsonb NOT NULL DEFAULT '{}'::jsonb, checkpoint jsonb NOT NULL DEFAULT '{}'::jsonb, available_at timestamptz NOT NULL DEFAULT now(),
 holder text, lease_until timestamptz, fence bigint NOT NULL DEFAULT 0, attempt int NOT NULL DEFAULT 0 CHECK(attempt>=0),
 max_attempt int NOT NULL DEFAULT 5 CHECK(max_attempt>0), error_code text, created_at timestamptz NOT NULL DEFAULT now(),
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,job_id),
 FOREIGN KEY(project_id,context_id) REFERENCES delivery.task_context(project_id,context_id)
);
CREATE TABLE ops.outbox (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 event_id uuid NOT NULL DEFAULT gen_random_uuid(),
 event_type text NOT NULL, aggregate_id uuid, aggregate_revision bigint NOT NULL, schema_version text NOT NULL,
 payload jsonb NOT NULL, occurred_at timestamptz NOT NULL DEFAULT now(), state text NOT NULL CHECK(state IN ('queued','running','succeeded','failed')), available_at timestamptz NOT NULL DEFAULT now(), lease_until timestamptz,
 fence bigint NOT NULL DEFAULT 0, attempt int NOT NULL DEFAULT 0,
 PRIMARY KEY(project_id,event_id)
);
CREATE TABLE ops.inbox (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 inbox_id uuid NOT NULL DEFAULT gen_random_uuid(),
 consumer_key text NOT NULL, event_id uuid NOT NULL, processed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,inbox_id),
 UNIQUE(project_id,consumer_key,event_id)
);
CREATE TABLE ops.idempotency (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 record_id uuid NOT NULL DEFAULT gen_random_uuid(),
 principal_id uuid NOT NULL REFERENCES identity.principal(principal_id), operation_id text NOT NULL,
 idempotency_key text NOT NULL, request_sha256 text NOT NULL CHECK(request_sha256 ~ '^[0-9a-f]{64}$'), result_ref jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,record_id),
 UNIQUE(project_id,principal_id,operation_id,idempotency_key)
);
CREATE TABLE ops.audit_event (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 audit_id uuid NOT NULL DEFAULT gen_random_uuid(),
 actor_id uuid REFERENCES identity.principal(principal_id), action text NOT NULL,
 resource_ref jsonb NOT NULL DEFAULT '{}'::jsonb, result_code text NOT NULL, request_id text NOT NULL, trace_id text, safe_metadata jsonb NOT NULL DEFAULT '{}'::jsonb, occurred_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,audit_id)
);
CREATE TABLE ops.notification (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 notification_id uuid NOT NULL DEFAULT gen_random_uuid(),
 principal_id uuid NOT NULL REFERENCES identity.principal(principal_id), kind text NOT NULL, opaque_ref text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), read_at timestamptz,
 version bigint NOT NULL DEFAULT 1 CHECK(version>0),
 PRIMARY KEY(project_id,notification_id)
);
CREATE TABLE ops.error_pattern (
 project_id uuid NOT NULL REFERENCES identity.project(project_id),
 pattern_id uuid NOT NULL DEFAULT gen_random_uuid(),
 pattern_key text NOT NULL, pattern_revision int NOT NULL CHECK(pattern_revision>0),
 applicability jsonb NOT NULL DEFAULT '{}'::jsonb, symptom text NOT NULL, root_cause text NOT NULL, detector text NOT NULL,
 mitigation text NOT NULL, regression_test_id text NOT NULL, evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb, state text NOT NULL CHECK(state IN ('candidate','reviewed','retired')), created_by uuid NOT NULL REFERENCES identity.principal(principal_id), created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(project_id,pattern_id),
 UNIQUE(project_id,pattern_key,pattern_revision)
);
CREATE TABLE read_model.search_document (
 project_id uuid NOT NULL REFERENCES identity.project(project_id), baseline_id uuid NOT NULL, unit_id uuid NOT NULL, revision_id uuid NOT NULL,
 title text NOT NULL, aliases text[] NOT NULL DEFAULT ARRAY[]::text[], searchable_text text NOT NULL,
 tokens tsvector GENERATED ALWAYS AS (to_tsvector('simple',searchable_text)) STORED,
 PRIMARY KEY(project_id,baseline_id,unit_id),
 FOREIGN KEY(project_id,baseline_id,unit_id,revision_id) REFERENCES gov.baseline_entry(project_id,baseline_id,unit_id,revision_id),
 FOREIGN KEY(project_id,unit_id,revision_id) REFERENCES core.revision(project_id,unit_id,revision_id)
);
CREATE INDEX ix_membership_principal ON identity.membership(principal_id,project_id) WHERE active;
CREATE INDEX ix_relation_from ON core.relation_index(project_id,from_unit_id,predicate);
CREATE INDEX ix_relation_to ON core.relation_index(project_id,to_unit_id,predicate);
CREATE INDEX ix_refs_target ON core.revision_ref(project_id,target_unit_id,ref_kind);
CREATE INDEX ix_revisions_unit ON core.revision(project_id,unit_id,rev_no DESC);
CREATE INDEX ix_baseline_revision ON gov.baseline_entry(project_id,revision_id);
CREATE INDEX ix_changes_state ON gov.change_set(project_id,state,created_at);
CREATE INDEX ix_tasks_state ON collab.task(project_id,state,created_at);
CREATE INDEX ix_impact_task ON collab.impact(project_id,task_id,disposition);
CREATE INDEX ix_context_unit ON delivery.context_dependency(project_id,unit_id,revision_id);
CREATE INDEX ix_job_queue ON ops.job(state,available_at,lease_until);
CREATE INDEX ix_outbox_queue ON ops.outbox(state,available_at,lease_until);
CREATE INDEX ix_work_queue ON agent.work_request(project_id,state,lease_until);
CREATE INDEX ix_audit_time ON ops.audit_event(project_id,occurred_at DESC,audit_id);
CREATE INDEX ix_search_tokens ON read_model.search_document USING gin(tokens);
CREATE INDEX ix_search_trgm ON read_model.search_document USING gin(title gin_trgm_ops);
CREATE INDEX ix_search_body_trgm ON read_model.search_document USING gin(searchable_text gin_trgm_ops);
-- Immutable facts and sealed snapshots, even when a mistaken ORM tries UPDATE/DELETE.
CREATE FUNCTION ops.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'immutable record' USING ERRCODE='55000'; END; $$;
CREATE FUNCTION ops.seal_once() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'immutable sealed record' USING ERRCODE='55000'; END IF;
 IF OLD.sealed OR NOT NEW.sealed OR (to_jsonb(NEW)-'sealed') <> (to_jsonb(OLD)-'sealed') THEN
   RAISE EXCEPTION 'only unsealed-to-sealed transition is permitted' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER revision_seal BEFORE UPDATE OR DELETE ON core.revision FOR EACH ROW EXECUTE FUNCTION ops.seal_once();
CREATE TRIGGER baseline_seal BEFORE UPDATE OR DELETE ON gov.baseline FOR EACH ROW EXECUTE FUNCTION ops.seal_once();
CREATE FUNCTION ops.assert_parent_unsealed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE is_sealed boolean; id uuid;
BEGIN
 id := (to_jsonb(NEW)->>TG_ARGV[0])::uuid;
 IF TG_ARGV[1]='baseline' THEN
   SELECT sealed INTO is_sealed FROM gov.baseline WHERE project_id=NEW.project_id AND baseline_id=id FOR UPDATE;
 ELSE
   SELECT sealed INTO is_sealed FROM core.revision WHERE project_id=NEW.project_id AND revision_id=id FOR UPDATE;
 END IF;
 IF is_sealed IS DISTINCT FROM false THEN RAISE EXCEPTION 'parent missing or sealed' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END; $$;
CREATE FUNCTION ops.assert_baseline_revision_sealed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM core.revision r WHERE r.project_id=NEW.project_id AND r.unit_id=NEW.unit_id AND r.revision_id=NEW.revision_id AND r.sealed) THEN
   RAISE EXCEPTION 'baseline entry must refer to a sealed revision' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER entry_revision_sealed BEFORE INSERT ON gov.baseline_entry FOR EACH ROW EXECUTE FUNCTION ops.assert_baseline_revision_sealed();
CREATE TRIGGER source_blob_parent BEFORE INSERT ON intake.source_blob_link FOR EACH ROW EXECUTE FUNCTION ops.assert_parent_unsealed('source_revision_id','revision');
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON intake.source_blob_link FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.proposal_intake FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER insert_entry BEFORE INSERT ON gov.baseline_entry FOR EACH ROW EXECUTE FUNCTION ops.assert_parent_unsealed('baseline_id','baseline');
CREATE TRIGGER insert_ref BEFORE INSERT ON core.revision_ref FOR EACH ROW EXECUTE FUNCTION ops.assert_parent_unsealed('source_revision_id','revision');
CREATE TRIGGER insert_relation BEFORE INSERT ON core.relation_index FOR EACH ROW EXECUTE FUNCTION ops.assert_parent_unsealed('relation_revision_id','revision');
CREATE TRIGGER insert_evidence BEFORE INSERT ON core.evidence_link FOR EACH ROW EXECUTE FUNCTION ops.assert_parent_unsealed('fact_revision_id','revision');
CREATE FUNCTION ops.check_baseline_pointer() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.current_baseline_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM gov.baseline b WHERE b.project_id=NEW.project_id AND b.baseline_id=NEW.current_baseline_id AND b.sealed) THEN
   RAISE EXCEPTION 'current baseline must be sealed' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER current_baseline_sealed BEFORE INSERT OR UPDATE OF current_baseline_id ON identity.project FOR EACH ROW EXECUTE FUNCTION ops.check_baseline_pointer();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON core.revision_ref FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON core.relation_index FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON core.evidence_link FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON core.external_identity FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON gov.review_decision FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON gov.baseline_entry FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON collab.investigation FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.task_context FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.context_dependency FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.context_invalidation FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.conformance FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.acceptance FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON delivery.deployment_record FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON agent.run_event FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON assistant.message FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON assistant.citation FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON ops.inbox FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON ops.idempotency FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON ops.audit_event FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
CREATE TRIGGER immutable_row BEFORE UPDATE OR DELETE ON ops.error_pattern FOR EACH ROW EXECUTE FUNCTION ops.reject_mutation();
-- App obtains atlas.project_id and atlas.subject_id from server-verified context, SET LOCAL inside a transaction.
-- Missing context deliberately denies. Provision non-owner, NOBYPASSRLS roles and least grants in deployment migration.
-- Project directory SELECT may read current subject's own memberships; no cross-project business-table access.
CREATE FUNCTION access.current_project() RETURNS uuid LANGUAGE sql STABLE AS $$ SELECT nullif(current_setting('atlas.project_id',true),'')::uuid $$;
CREATE FUNCTION access.current_subject() RETURNS uuid LANGUAGE sql STABLE AS $$ SELECT nullif(current_setting('atlas.subject_id',true),'')::uuid $$;
ALTER TABLE identity.project ENABLE ROW LEVEL SECURITY;
ALTER TABLE identity.project FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON identity.project USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE identity.membership ENABLE ROW LEVEL SECURITY;
ALTER TABLE identity.membership FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON identity.membership USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.unit ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.unit FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.unit USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE access.grant ENABLE ROW LEVEL SECURITY;
ALTER TABLE access.grant FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON access.grant USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.revision ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.revision FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.revision USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.revision_ref ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.revision_ref FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.revision_ref USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.relation_index ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.relation_index FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.relation_index USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.evidence_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.evidence_link FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.evidence_link USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE core.external_identity ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.external_identity FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON core.external_identity USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE gov.change_set ENABLE ROW LEVEL SECURITY;
ALTER TABLE gov.change_set FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON gov.change_set USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE gov.change_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE gov.change_item FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON gov.change_item USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE gov.review_decision ENABLE ROW LEVEL SECURITY;
ALTER TABLE gov.review_decision FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON gov.review_decision USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE gov.baseline ENABLE ROW LEVEL SECURITY;
ALTER TABLE gov.baseline FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON gov.baseline USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE gov.baseline_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE gov.baseline_entry FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON gov.baseline_entry USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE intake.blob ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.blob FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON intake.blob USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE intake.upload ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.upload FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON intake.upload USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE intake.import_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.import_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON intake.import_batch USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE intake.import_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.import_entry FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON intake.import_entry USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.task ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.task FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.task USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.issue ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.issue FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.issue USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.feedback ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.feedback FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.feedback USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.comment ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.comment FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.comment USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.investigation ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.investigation FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.investigation USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE collab.impact ENABLE ROW LEVEL SECURITY;
ALTER TABLE collab.impact FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON collab.impact USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.task_context ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.task_context FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.task_context USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.context_dependency ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.context_dependency FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.context_dependency USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.context_invalidation ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.context_invalidation FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.context_invalidation USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.conformance ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.conformance FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.conformance USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.acceptance ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.acceptance FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.acceptance USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.delivery_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.delivery_record FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.delivery_record USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.deployment_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.deployment_record FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.deployment_record USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE delivery.knowledge_debt ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.knowledge_debt FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.knowledge_debt USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE agent.run ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent.run FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON agent.run USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE agent.run_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent.run_event FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON agent.run_event USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE agent.work_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent.work_request FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON agent.work_request USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE assistant.provider ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.provider FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON assistant.provider USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE assistant.conversation ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.conversation FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON assistant.conversation USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE assistant.message ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.message FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON assistant.message USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE assistant.citation ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.citation FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON assistant.citation USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.job ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.job FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.job USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.outbox FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.outbox USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.inbox FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.inbox USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.idempotency ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.idempotency FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.idempotency USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.audit_event FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.audit_event USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.notification ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.notification FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.notification USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE ops.error_pattern ENABLE ROW LEVEL SECURITY;
ALTER TABLE ops.error_pattern FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON ops.error_pattern USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
ALTER TABLE read_model.search_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE read_model.search_document FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON read_model.search_document USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
CREATE POLICY own_memberships_directory ON identity.membership FOR SELECT USING (principal_id=access.current_subject() AND active);
CREATE POLICY own_projects_directory ON identity.project FOR SELECT USING (
 EXISTS(SELECT 1 FROM identity.membership m WHERE m.project_id=project.project_id AND m.principal_id=access.current_subject() AND m.active)
);
-- Worker claim role: explicit narrow PG role with SELECT/UPDATE on ops.job/outbox only, matching dedicated RLS policies;
-- implement claim functions/least grants in secure deployment migration. Do not grant BYPASSRLS or broad business-table access.
-- Integration gate: application must verify all refs and seals, rule schemas, approval digest, current epoch and scopes;
-- SQL alone does not validate all semantic/authorization invariants. Do not grant TRUNCATE/DDL to app roles.
ALTER TABLE intake.source_blob_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.source_blob_link FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON intake.source_blob_link USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());

ALTER TABLE delivery.proposal_intake ENABLE ROW LEVEL SECURITY;
ALTER TABLE delivery.proposal_intake FORCE ROW LEVEL SECURITY;
CREATE POLICY project_boundary ON delivery.proposal_intake USING (project_id=access.current_project()) WITH CHECK (project_id=access.current_project());
COMMIT;
