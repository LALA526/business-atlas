using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace BuildingBlocks.Persistence;

/// <summary>
/// Persistence model for the Atlas physical schema. Shared-type entities keep the
/// infrastructure independent from module domain objects while retaining exact
/// PostgreSQL table, key and column mappings.
/// </summary>
public sealed class AtlasDbContext(DbContextOptions<AtlasDbContext> options) : DbContext(options)
{
    public DbSet<Dictionary<string, object>> Table(string schema, string table) =>
        Set<Dictionary<string, object>>($"{schema}.{table}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var definition in AtlasSchema.Tables)
        {
            var parts = definition.Split('|');
            var name = parts[0];
            var separator = name.IndexOf('.');
            var entity = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(name, builder =>
            {
                builder.ToTable(name[(separator + 1)..], name[..separator]);
                foreach (var column in parts[1].Split(','))
                {
                    var columnParts = column.Split(':');
                    var databaseType = columnParts[1];
                    var nullable = databaseType.EndsWith('?');
                    var propertyType = AtlasSchema.ClrType(databaseType.TrimEnd('?'));
                    if (nullable && propertyType.IsValueType)
                    {
                        propertyType = typeof(Nullable<>).MakeGenericType(propertyType);
                    }
                    var property = builder.Property(propertyType, columnParts[0])
                        .HasColumnType(databaseType.TrimEnd('?'))
                        .IsRequired(!nullable);
                    if (propertyType == typeof(NpgsqlTsVector))
                    {
                        property.ValueGeneratedOnAddOrUpdate();
                    }
                }

                builder.HasKey(parts[2].Split(','));
                if (parts.Length > 3 && parts[3].Length > 0)
                {
                    foreach (var unique in parts[3].Split(';'))
                    {
                        builder.HasIndex(unique.Split(',')).IsUnique();
                    }
                }
            });
            _ = entity;
        }

        foreach (var relation in AtlasSchema.Relations)
        {
            var parts = relation.Split('|');
            var targetColumns = parts[3].Length == 0 ? null : parts[3].Split(',');
            var relationship = modelBuilder.Entity(parts[0])
                .HasOne(parts[1], null)
                .WithMany()
                .HasForeignKey(parts[2].Split(','))
                .OnDelete(DeleteBehavior.Restrict);
            if (targetColumns is not null)
            {
                relationship.HasPrincipalKey(targetColumns);
            }
        }

        foreach (var index in AtlasSchema.Indexes)
        {
            var parts = index.Split('|');
            var item = modelBuilder.Entity(parts[0]).HasIndex(parts[1].Split(','), parts[2]);
            if (parts.Length > 3)
            {
                item.HasFilter(parts[3]);
            }
        }
    }
}

internal static class AtlasSchema
{
    internal static Type ClrType(string databaseType) => databaseType switch
    {
        "uuid" => typeof(Guid),
        "bigint" => typeof(long),
        "int" => typeof(int),
        "boolean" => typeof(bool),
        "timestamptz" => typeof(DateTimeOffset),
        "tsvector" => typeof(NpgsqlTsVector),
        "text[]" => typeof(string[]),
        _ => typeof(string),
    };

    // name|column:databaseType (? marks nullable)|primary key|unique indexes (; separated)
    internal static readonly string[] Tables =
    [
        "identity.principal|principal_id:uuid,issuer:text,subject:text,kind:text,active:boolean,created_at:timestamptz|principal_id|issuer,subject",
        "identity.project|project_id:uuid,slug:text,display_name:text,owner_id:uuid,version:bigint,policy_epoch:bigint,current_baseline_id:uuid?,created_at:timestamptz,archived_at:timestamptz?|project_id|slug",
        "identity.membership|project_id:uuid,principal_id:uuid,roles:text[],clearance:int,active:boolean,version:bigint|project_id,principal_id",
        "core.unit|project_id:uuid,unit_id:uuid,kind:text,owner_id:uuid,visibility:int,archived_at:timestamptz?,created_by:uuid,created_at:timestamptz,version:bigint|project_id,unit_id",
        "access.grant|project_id:uuid,grant_id:uuid,unit_id:uuid,principal_id:uuid,action:text,field_paths:text[],effect:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,grant_id|project_id,unit_id,principal_id,action,effect",
        "core.revision|project_id:uuid,revision_id:uuid,unit_id:uuid,rev_no:bigint,parent_revision_id:uuid?,schema_type:text,schema_version:text,schema_definition_revision_id:uuid?,fact_kind:text,verification:text,payload:jsonb,applicability:jsonb,content_sha256:text,sealed:boolean,created_by:uuid,created_at:timestamptz|project_id,revision_id|project_id,unit_id,rev_no;project_id,unit_id,revision_id",
        "core.revision_ref|project_id:uuid,ref_id:uuid,source_revision_id:uuid,ref_key:text,ref_kind:text,target_unit_id:uuid,target_revision_id:uuid?,required:boolean|project_id,ref_id|project_id,source_revision_id,ref_key",
        "core.relation_index|project_id:uuid,relation_revision_id:uuid,predicate:text,from_unit_id:uuid,to_unit_id:uuid|project_id,relation_revision_id",
        "core.evidence_link|project_id:uuid,evidence_id:uuid,fact_revision_id:uuid,source_revision_id:uuid,locator:jsonb,support:text,capture_kind:text,created_by:uuid,created_at:timestamptz|project_id,evidence_id",
        "core.external_identity|project_id:uuid,mapping_id:uuid,source_namespace:text,external_type:text,external_id:text,unit_id:uuid|project_id,mapping_id|project_id,source_namespace,external_type,external_id",
        "gov.change_set|project_id:uuid,change_set_id:uuid,base_baseline_id:uuid,title:text,state:text,submitted_seq:bigint?,submit_digest:text?,review_requirements:jsonb,summary:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,change_set_id",
        "gov.change_item|project_id:uuid,item_id:uuid,change_set_id:uuid,unit_id:uuid,base_revision_id:uuid?,proposed_revision_id:uuid?,action:text,dependency_group:text,reason:text|project_id,item_id|project_id,change_set_id,unit_id",
        "gov.review_decision|project_id:uuid,decision_id:uuid,change_set_id:uuid,submitted_seq:bigint,submit_digest:text,approval_role:text,scope:jsonb,decision:text,reason:text,anchor:jsonb,actor_id:uuid,decided_at:timestamptz|project_id,decision_id",
        "gov.baseline|project_id:uuid,baseline_id:uuid,baseline_no:bigint,parent_baseline_id:uuid?,change_set_id:uuid?,registry_version:text,digest:text,sealed:boolean,created_by:uuid,created_at:timestamptz|project_id,baseline_id|project_id,baseline_no;project_id,change_set_id",
        "gov.baseline_entry|project_id:uuid,baseline_id:uuid,unit_id:uuid,revision_id:uuid|project_id,baseline_id,unit_id|project_id,baseline_id,unit_id,revision_id",
        "intake.blob|project_id:uuid,blob_id:uuid,sha256:text,byte_size:bigint,storage_key:text,scan_status:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,blob_id|project_id,sha256;project_id,storage_key",
        "intake.source_blob_link|project_id:uuid,source_revision_id:uuid,blob_id:uuid|project_id,source_revision_id,blob_id",
        "intake.upload|project_id:uuid,upload_id:uuid,blob_id:uuid?,original_name:text,content_type:text,expires_at:timestamptz,state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,upload_id",
        "intake.import_batch|project_id:uuid,import_id:uuid,package_id:text,format_version:text,package_sha256:text,source_namespace:text,blob_id:uuid,base_baseline_id:uuid,change_set_id:uuid?,state:text,summary:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,import_id|project_id,source_namespace,package_id;project_id,source_namespace,package_sha256",
        "intake.import_entry|project_id:uuid,entry_id:uuid,import_id:uuid,external_type:text,external_id:text,unit_id:uuid?,diff:jsonb,decision:text?,reason:text?,decided_by:uuid?,version:bigint|project_id,entry_id|project_id,import_id,external_type,external_id",
        "collab.task|project_id:uuid,task_id:uuid,root_unit_id:uuid?,base_baseline_id:uuid,intent:text,title:text,classification:text,state:text,external_ref:jsonb,target_summary:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,task_id",
        "collab.issue|project_id:uuid,issue_id:uuid,unit_id:uuid?,revision_id:uuid?,task_id:uuid?,change_set_id:uuid?,anchor:jsonb,body:text,state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,issue_id",
        "collab.feedback|project_id:uuid,feedback_id:uuid,unit_id:uuid?,revision_id:uuid?,task_id:uuid?,change_set_id:uuid?,anchor:jsonb,body:text,state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,feedback_id",
        "collab.comment|project_id:uuid,comment_id:uuid,unit_id:uuid?,revision_id:uuid?,task_id:uuid?,change_set_id:uuid?,anchor:jsonb,body:text,state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,comment_id",
        "collab.investigation|project_id:uuid,investigation_id:uuid,task_id:uuid,base_baseline_id:uuid,scope:jsonb,evidence_refs:jsonb,coverage:jsonb,capture_kind:text,created_by:uuid,created_at:timestamptz|project_id,investigation_id",
        "collab.impact|project_id:uuid,impact_id:uuid,task_id:uuid,target_unit_id:uuid?,target_revision_id:uuid?,locator:jsonb,path:jsonb,disposition:text,inspection_state:text,investigation_id:uuid?,reason:text,risk:text,assigned_task_id:uuid?,evidence_refs:jsonb,close_state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,impact_id",
        "delivery.task_context|project_id:uuid,context_id:uuid,task_id:uuid,baseline_id:uuid,policy_epoch:bigint,phase:text,contract_digest:text,body:jsonb,expires_at:timestamptz?,created_by:uuid,created_at:timestamptz|project_id,context_id",
        "delivery.context_dependency|project_id:uuid,dependency_id:uuid,context_id:uuid,unit_id:uuid,revision_id:uuid,required:boolean,purpose:text|project_id,dependency_id|project_id,context_id,unit_id,revision_id,purpose",
        "delivery.context_invalidation|project_id:uuid,invalidation_id:uuid,context_id:uuid,cause:text,event_id:uuid?,invalidated_at:timestamptz|project_id,invalidation_id",
        "delivery.conformance|project_id:uuid,check_id:uuid,normative_revision_id:uuid,observed_revision_id:uuid,build_digest:text,environment:text,context_sha256:text,evidence_sha256:text,evidence_refs:jsonb,expected:jsonb,actual:jsonb,verdict:text,method:text,created_by:uuid,created_at:timestamptz|project_id,check_id",
        "delivery.acceptance|project_id:uuid,acceptance_id:uuid,task_id:uuid,context_id:uuid,test_id:text,expected:jsonb,observed:jsonb?,result:text,build_digest:text?,environment:text?,evidence_refs:jsonb,created_by:uuid,created_at:timestamptz|project_id,acceptance_id",
        "delivery.proposal_intake|project_id:uuid,proposal_sha256:text,proposal_external_id:text,schema_version:text,task_id:uuid,repo_key:text,blob_id:uuid,created_by:uuid,created_at:timestamptz|project_id,proposal_sha256|project_id,repo_key,proposal_external_id",
        "delivery.delivery_record|project_id:uuid,delivery_id:uuid,external_delivery_id:text,task_id:uuid,repo_key:text,final_commit_sha:text,manifest_sha256:text,build_digest:text,proposal_sha256:text,blob_id:uuid,change_set_id:uuid?,created_by:uuid,created_at:timestamptz|project_id,delivery_id|project_id,repo_key,external_delivery_id",
        "delivery.deployment_record|project_id:uuid,deployment_id:uuid,environment:text,repo_key:text,final_commit_sha:text,build_digest:text,migration_version:text,config_digest:text,previous_deployment_id:uuid?,deployed_at:timestamptz,evidence_refs:jsonb,created_by:uuid,created_at:timestamptz|project_id,deployment_id",
        "delivery.knowledge_debt|project_id:uuid,debt_id:uuid,task_id:uuid,fix_task_id:uuid?,owner_id:uuid,due_at:timestamptz,approval_ref:text,risk:text,reason:text,state:text,created_by:uuid,created_at:timestamptz,version:bigint|project_id,debt_id",
        "agent.run|project_id:uuid,run_id:uuid,task_id:uuid?,context_id:uuid?,executor_id:uuid,state:text,provider:text?,model:text?,checkpoint:jsonb,budget:jsonb,usage:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,run_id",
        "agent.run_event|project_id:uuid,event_id:uuid,run_id:uuid,seq:bigint,external_event_id:text,capture_kind:text,event_type:text,event_data:jsonb,artifact_sha256:text?,occurred_at:timestamptz,created_by:uuid,created_at:timestamptz|project_id,event_id|project_id,run_id,seq;project_id,run_id,external_event_id",
        "agent.work_request|project_id:uuid,work_id:uuid,task_id:uuid,context_id:uuid,run_id:uuid?,kind:text,scope:jsonb,budget:jsonb,deadline:timestamptz?,state:text,holder_id:uuid?,lease_until:timestamptz?,fence:bigint,attempt:int,checkpoint:jsonb,result_ref:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,work_id",
        "assistant.provider|project_id:uuid,provider_id:uuid,provider_key:text,endpoint:text,model:text,secret_ref:text,allowed:boolean,options:jsonb,created_by:uuid,created_at:timestamptz,version:bigint|project_id,provider_id|project_id,provider_key",
        "assistant.conversation|project_id:uuid,conversation_id:uuid,root_unit_id:uuid?,baseline_id:uuid,title:text,owner_id:uuid,created_by:uuid,created_at:timestamptz,version:bigint|project_id,conversation_id",
        "assistant.message|project_id:uuid,message_id:uuid,conversation_id:uuid,run_id:uuid?,seq:bigint,kind:text,body:jsonb,created_by:uuid,created_at:timestamptz|project_id,message_id|project_id,conversation_id,seq",
        "assistant.citation|project_id:uuid,citation_id:uuid,message_id:uuid,revision_id:uuid,locator:jsonb,block_id:text|project_id,citation_id",
        "ops.job|project_id:uuid,job_id:uuid,kind:text,requester_id:uuid,context_id:uuid?,state:text,input:jsonb,result:jsonb,checkpoint:jsonb,available_at:timestamptz,holder:text?,lease_until:timestamptz?,fence:bigint,attempt:int,max_attempt:int,error_code:text?,created_at:timestamptz,version:bigint|project_id,job_id",
        "ops.outbox|project_id:uuid,event_id:uuid,event_type:text,aggregate_id:uuid?,aggregate_revision:bigint,schema_version:text,payload:jsonb,occurred_at:timestamptz,state:text,available_at:timestamptz,lease_until:timestamptz?,fence:bigint,attempt:int|project_id,event_id",
        "ops.inbox|project_id:uuid,inbox_id:uuid,consumer_key:text,event_id:uuid,processed_at:timestamptz|project_id,inbox_id|project_id,consumer_key,event_id",
        "ops.idempotency|project_id:uuid,record_id:uuid,principal_id:uuid,operation_id:text,idempotency_key:text,request_sha256:text,result_ref:jsonb,created_at:timestamptz|project_id,record_id|project_id,principal_id,operation_id,idempotency_key",
        "ops.audit_event|project_id:uuid,audit_id:uuid,actor_id:uuid?,action:text,resource_ref:jsonb,result_code:text,request_id:text,trace_id:text?,safe_metadata:jsonb,occurred_at:timestamptz|project_id,audit_id",
        "ops.notification|project_id:uuid,notification_id:uuid,principal_id:uuid,kind:text,opaque_ref:text,created_at:timestamptz,read_at:timestamptz?,version:bigint|project_id,notification_id",
        "ops.error_pattern|project_id:uuid,pattern_id:uuid,pattern_key:text,pattern_revision:int,applicability:jsonb,symptom:text,root_cause:text,detector:text,mitigation:text,regression_test_id:text,evidence_refs:jsonb,state:text,created_by:uuid,created_at:timestamptz|project_id,pattern_id|project_id,pattern_key,pattern_revision",
        "read_model.search_document|project_id:uuid,baseline_id:uuid,unit_id:uuid,revision_id:uuid,title:text,aliases:text[],searchable_text:text,tokens:tsvector|project_id,baseline_id,unit_id",
    ];

    // source entity|target entity|source columns|target columns (blank = target primary key)
    internal static readonly string[] Relations =
    [
        "identity.project|identity.principal|owner_id|",
        "identity.membership|identity.project|project_id|",
        "identity.membership|identity.principal|principal_id|",
        "identity.project|gov.baseline|project_id,current_baseline_id|project_id,baseline_id",
        "core.unit|identity.project|project_id|",
        "core.unit|identity.principal|owner_id|",
        "core.unit|identity.principal|created_by|",
        "access.grant|identity.project|project_id|",
        "access.grant|identity.principal|principal_id|",
        "access.grant|identity.principal|created_by|",
        "access.grant|core.unit|project_id,unit_id|project_id,unit_id",
        "core.revision|identity.project|project_id|",
        "core.revision|core.unit|project_id,unit_id|project_id,unit_id",
        "core.revision|identity.principal|created_by|",
        "core.revision|core.revision|project_id,unit_id,parent_revision_id|project_id,unit_id,revision_id",
        "core.revision|core.revision|project_id,schema_definition_revision_id|project_id,revision_id",
        "core.revision_ref|identity.project|project_id|",
        "core.revision_ref|core.revision|project_id,source_revision_id|project_id,revision_id",
        "core.revision_ref|core.unit|project_id,target_unit_id|project_id,unit_id",
        "core.revision_ref|core.revision|project_id,target_unit_id,target_revision_id|project_id,unit_id,revision_id",
        "core.relation_index|identity.project|project_id|",
        "core.relation_index|core.revision|project_id,relation_revision_id|project_id,revision_id",
        "core.relation_index|core.unit|project_id,from_unit_id|project_id,unit_id",
        "core.relation_index|core.unit|project_id,to_unit_id|project_id,unit_id",
        "core.evidence_link|identity.project|project_id|",
        "core.evidence_link|core.revision|project_id,fact_revision_id|project_id,revision_id",
        "core.evidence_link|core.revision|project_id,source_revision_id|project_id,revision_id",
        "core.evidence_link|identity.principal|created_by|",
        "core.external_identity|identity.project|project_id|",
        "core.external_identity|core.unit|project_id,unit_id|project_id,unit_id",
        "gov.change_set|identity.project|project_id|",
        "gov.change_set|identity.principal|created_by|",
        "gov.change_set|gov.baseline|project_id,base_baseline_id|project_id,baseline_id",
        "gov.change_item|identity.project|project_id|",
        "gov.change_item|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "gov.change_item|core.unit|project_id,unit_id|project_id,unit_id",
        "gov.change_item|core.revision|project_id,unit_id,base_revision_id|project_id,unit_id,revision_id",
        "gov.change_item|core.revision|project_id,unit_id,proposed_revision_id|project_id,unit_id,revision_id",
        "gov.review_decision|identity.project|project_id|",
        "gov.review_decision|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "gov.review_decision|identity.principal|actor_id|",
        "gov.baseline|identity.project|project_id|",
        "gov.baseline|gov.baseline|project_id,parent_baseline_id|project_id,baseline_id",
        "gov.baseline|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "gov.baseline|identity.principal|created_by|",
        "gov.baseline_entry|identity.project|project_id|",
        "gov.baseline_entry|gov.baseline|project_id,baseline_id|project_id,baseline_id",
        "gov.baseline_entry|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
        "intake.blob|identity.project|project_id|",
        "intake.blob|identity.principal|created_by|",
        "intake.source_blob_link|identity.project|project_id|",
        "intake.source_blob_link|core.revision|project_id,source_revision_id|project_id,revision_id",
        "intake.source_blob_link|intake.blob|project_id,blob_id|project_id,blob_id",
        "intake.upload|identity.project|project_id|",
        "intake.upload|intake.blob|project_id,blob_id|project_id,blob_id",
        "intake.upload|identity.principal|created_by|",
        "intake.import_batch|identity.project|project_id|",
        "intake.import_batch|intake.blob|project_id,blob_id|project_id,blob_id",
        "intake.import_batch|gov.baseline|project_id,base_baseline_id|project_id,baseline_id",
        "intake.import_batch|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "intake.import_batch|identity.principal|created_by|",
        "intake.import_entry|identity.project|project_id|",
        "intake.import_entry|intake.import_batch|project_id,import_id|project_id,import_id",
        "intake.import_entry|core.unit|project_id,unit_id|project_id,unit_id",
        "intake.import_entry|identity.principal|decided_by|",
        "collab.task|identity.project|project_id|",
        "collab.task|core.unit|project_id,root_unit_id|project_id,unit_id",
        "collab.task|gov.baseline|project_id,base_baseline_id|project_id,baseline_id",
        "collab.task|identity.principal|created_by|",
        "collab.issue|identity.project|project_id|",
        "collab.issue|core.unit|project_id,unit_id|project_id,unit_id",
        "collab.issue|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
        "collab.issue|collab.task|project_id,task_id|project_id,task_id",
        "collab.issue|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "collab.issue|identity.principal|created_by|",
        "collab.feedback|identity.project|project_id|",
        "collab.feedback|core.unit|project_id,unit_id|project_id,unit_id",
        "collab.feedback|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
        "collab.feedback|collab.task|project_id,task_id|project_id,task_id",
        "collab.feedback|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "collab.feedback|identity.principal|created_by|",
        "collab.comment|identity.project|project_id|",
        "collab.comment|core.unit|project_id,unit_id|project_id,unit_id",
        "collab.comment|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
        "collab.comment|collab.task|project_id,task_id|project_id,task_id",
        "collab.comment|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "collab.comment|identity.principal|created_by|",
        "collab.investigation|identity.project|project_id|",
        "collab.investigation|collab.task|project_id,task_id|project_id,task_id",
        "collab.investigation|gov.baseline|project_id,base_baseline_id|project_id,baseline_id",
        "collab.investigation|identity.principal|created_by|",
        "collab.impact|identity.project|project_id|",
        "collab.impact|collab.task|project_id,task_id|project_id,task_id",
        "collab.impact|core.unit|project_id,target_unit_id|project_id,unit_id",
        "collab.impact|core.revision|project_id,target_unit_id,target_revision_id|project_id,unit_id,revision_id",
        "collab.impact|collab.investigation|project_id,investigation_id|project_id,investigation_id",
        "collab.impact|collab.task|project_id,assigned_task_id|project_id,task_id",
        "collab.impact|identity.principal|created_by|",
        "delivery.task_context|identity.project|project_id|",
        "delivery.task_context|collab.task|project_id,task_id|project_id,task_id",
        "delivery.task_context|gov.baseline|project_id,baseline_id|project_id,baseline_id",
        "delivery.task_context|identity.principal|created_by|",
        "delivery.context_dependency|identity.project|project_id|",
        "delivery.context_dependency|delivery.task_context|project_id,context_id|project_id,context_id",
        "delivery.context_dependency|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
        "delivery.context_invalidation|identity.project|project_id|",
        "delivery.context_invalidation|delivery.task_context|project_id,context_id|project_id,context_id",
        "delivery.conformance|identity.project|project_id|",
        "delivery.conformance|core.revision|project_id,normative_revision_id|project_id,revision_id",
        "delivery.conformance|core.revision|project_id,observed_revision_id|project_id,revision_id",
        "delivery.conformance|identity.principal|created_by|",
        "delivery.acceptance|identity.project|project_id|",
        "delivery.acceptance|collab.task|project_id,task_id|project_id,task_id",
        "delivery.acceptance|delivery.task_context|project_id,context_id|project_id,context_id",
        "delivery.acceptance|identity.principal|created_by|",
        "delivery.proposal_intake|identity.project|project_id|",
        "delivery.proposal_intake|collab.task|project_id,task_id|project_id,task_id",
        "delivery.proposal_intake|intake.blob|project_id,blob_id|project_id,blob_id",
        "delivery.proposal_intake|identity.principal|created_by|",
        "delivery.delivery_record|identity.project|project_id|",
        "delivery.delivery_record|delivery.proposal_intake|project_id,proposal_sha256|project_id,proposal_sha256",
        "delivery.delivery_record|collab.task|project_id,task_id|project_id,task_id",
        "delivery.delivery_record|intake.blob|project_id,blob_id|project_id,blob_id",
        "delivery.delivery_record|gov.change_set|project_id,change_set_id|project_id,change_set_id",
        "delivery.delivery_record|identity.principal|created_by|",
        "delivery.deployment_record|identity.project|project_id|",
        "delivery.deployment_record|delivery.deployment_record|project_id,previous_deployment_id|project_id,deployment_id",
        "delivery.deployment_record|identity.principal|created_by|",
        "delivery.knowledge_debt|identity.project|project_id|",
        "delivery.knowledge_debt|collab.task|project_id,task_id|project_id,task_id",
        "delivery.knowledge_debt|collab.task|project_id,fix_task_id|project_id,task_id",
        "delivery.knowledge_debt|identity.principal|owner_id|",
        "delivery.knowledge_debt|identity.principal|created_by|",
        "agent.run|identity.project|project_id|",
        "agent.run|collab.task|project_id,task_id|project_id,task_id",
        "agent.run|delivery.task_context|project_id,context_id|project_id,context_id",
        "agent.run|identity.principal|executor_id|",
        "agent.run|identity.principal|created_by|",
        "agent.run_event|identity.project|project_id|",
        "agent.run_event|agent.run|project_id,run_id|project_id,run_id",
        "agent.run_event|identity.principal|created_by|",
        "agent.work_request|identity.project|project_id|",
        "agent.work_request|collab.task|project_id,task_id|project_id,task_id",
        "agent.work_request|delivery.task_context|project_id,context_id|project_id,context_id",
        "agent.work_request|agent.run|project_id,run_id|project_id,run_id",
        "agent.work_request|identity.principal|holder_id|",
        "agent.work_request|identity.principal|created_by|",
        "assistant.provider|identity.project|project_id|",
        "assistant.provider|identity.principal|created_by|",
        "assistant.conversation|identity.project|project_id|",
        "assistant.conversation|core.unit|project_id,root_unit_id|project_id,unit_id",
        "assistant.conversation|gov.baseline|project_id,baseline_id|project_id,baseline_id",
        "assistant.conversation|identity.principal|owner_id|",
        "assistant.conversation|identity.principal|created_by|",
        "assistant.message|identity.project|project_id|",
        "assistant.message|assistant.conversation|project_id,conversation_id|project_id,conversation_id",
        "assistant.message|agent.run|project_id,run_id|project_id,run_id",
        "assistant.message|identity.principal|created_by|",
        "assistant.citation|identity.project|project_id|",
        "assistant.citation|assistant.message|project_id,message_id|project_id,message_id",
        "assistant.citation|core.revision|project_id,revision_id|project_id,revision_id",
        "ops.job|identity.project|project_id|",
        "ops.job|identity.principal|requester_id|",
        "ops.job|delivery.task_context|project_id,context_id|project_id,context_id",
        "ops.outbox|identity.project|project_id|",
        "ops.inbox|identity.project|project_id|",
        "ops.idempotency|identity.project|project_id|",
        "ops.idempotency|identity.principal|principal_id|",
        "ops.audit_event|identity.project|project_id|",
        "ops.audit_event|identity.principal|actor_id|",
        "ops.notification|identity.project|project_id|",
        "ops.notification|identity.principal|principal_id|",
        "ops.error_pattern|identity.project|project_id|",
        "ops.error_pattern|identity.principal|created_by|",
        "read_model.search_document|identity.project|project_id|",
        "read_model.search_document|gov.baseline_entry|project_id,baseline_id,unit_id,revision_id|project_id,baseline_id,unit_id,revision_id",
        "read_model.search_document|core.revision|project_id,unit_id,revision_id|project_id,unit_id,revision_id",
    ];

    // entity|columns|name|optional SQL filter
    internal static readonly string[] Indexes =
    [
        "identity.membership|principal_id,project_id|ix_membership_principal|active",
        "core.relation_index|project_id,from_unit_id,predicate|ix_relation_from",
        "core.relation_index|project_id,to_unit_id,predicate|ix_relation_to",
        "core.revision_ref|project_id,target_unit_id,ref_kind|ix_refs_target",
        "core.revision|project_id,unit_id,rev_no|ix_revisions_unit",
        "gov.baseline_entry|project_id,revision_id|ix_baseline_revision",
        "gov.change_set|project_id,state,created_at|ix_changes_state",
        "collab.task|project_id,state,created_at|ix_tasks_state",
        "collab.impact|project_id,task_id,disposition|ix_impact_task",
        "delivery.context_dependency|project_id,unit_id,revision_id|ix_context_unit",
        "ops.job|state,available_at,lease_until|ix_job_queue",
        "ops.outbox|state,available_at,lease_until|ix_outbox_queue",
        "agent.work_request|project_id,state,lease_until|ix_work_queue",
        "ops.audit_event|project_id,occurred_at,audit_id|ix_audit_time",
    ];
}
