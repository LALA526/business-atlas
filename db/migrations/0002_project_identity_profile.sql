BEGIN;
CREATE TABLE IF NOT EXISTS identity.project_profile (
 project_id uuid PRIMARY KEY REFERENCES identity.project(project_id),
 profile_kind text NOT NULL CHECK (profile_kind IN ('balanced', 'business', 'technical')),
 policy_version text NOT NULL CHECK (length(trim(policy_version)) > 0),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE POLICY membership_directory ON identity.membership FOR SELECT
 USING (principal_id = access.current_subject());
CREATE POLICY project_directory ON identity.project FOR SELECT
 USING (EXISTS (
  SELECT 1 FROM identity.membership m
  WHERE m.project_id = identity.project.project_id
    AND m.principal_id = access.current_subject() AND m.active
 ));
COMMIT;
