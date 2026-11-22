#!/usr/bin/env python3
"""Build a traceable public Self Project view and AKP candidate from repository evidence."""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
TRACE = "docs/baselines/T1.0/03_开发执行/requirements-trace.csv"
DAG = "docs/baselines/T1.0/03_开发执行/task-dag.json"
CHARTER = "docs/baselines/T1.0/05_原始基线/P1.0/BusinessAtlas_Product_Design_Baseline/01_概念体系/01_产品宪章与边界.md"
P1_README = "docs/baselines/T1.0/05_原始基线/P1.0/BusinessAtlas_Product_Design_Baseline/README.md"
V93_REVIEW = "docs/baselines/T1.0/05_原始基线/V9.3原评审报告.md"
HANDOFF = "docs/release/t1.0-handoff/HANDOFF.md"
ARCHITECTURE = "docs/baselines/T1.0/01_技术实施/01_目标架构与范围.md"
ENGINEERING = "docs/baselines/T1.0/01_技术实施/08_导入与工程知识交付.md"
PR_EVIDENCE = "knowledge/evidence/azure-pr-78.json"
BUILD_EVIDENCE = "knowledge/evidence/azure-build-103.json"
DESIGN_SOURCES = [TRACE, DAG, CHARTER, P1_README, V93_REVIEW, HANDOFF, ARCHITECTURE, ENGINEERING, PR_EVIDENCE, BUILD_EVIDENCE]
PUBLIC_DOCUMENTS = [CHARTER, P1_README, V93_REVIEW, HANDOFF, ARCHITECTURE, ENGINEERING]
CODE_SOURCES = [
    "README.md", "AGENTS.md", "azure-pipelines.yml",
    "apps/web/src/main.tsx", "apps/web/src/routes/ProjectsPage.tsx",
    "apps/web/src/features/project/ProjectShell.tsx", "apps/web/src/features/project/OverviewPage.tsx",
    "apps/web/src/features/project/CatalogPage.tsx", "apps/web/src/features/project/KnowledgePage.tsx",
    "apps/web/src/features/project/ImportWorkspace.tsx", "apps/web/src/features/review/ReviewWorkspace.tsx",
    "apps/web/src/features/public/SelfProjectPage.tsx", "apps/web/src/features/public/self-project.css",
    "apps/Atlas.Api/Program.cs", "apps/Atlas.Api/Auth/AtlasAuthentication.cs",
    "apps/Atlas.Api/Auth/IdentityEndpoints.cs", "apps/Atlas.Api/ReadSearchExplain/ReadSearchExplainEndpoints.cs",
    "src/Modules/ProjectIdentity/Infrastructure/PostgresProjectIdentityStore.cs",
    "src/Modules/KnowledgeCore/Infrastructure/PostgresKnowledgeStore.cs",
    "src/Modules/Governance/Infrastructure/PostgresGovernanceStore.cs",
    "scripts/self-project/build_case.py", "scripts/self-project/test_self_project.py",
    "contracts/openapi.yaml", "tests/Integration/ProjectIdentityTests.cs",
]
ALL_SOURCES = sorted(set(DESIGN_SOURCES + CODE_SOURCES))


def git(*args: str) -> str:
    return subprocess.check_output(["git", "-C", str(ROOT), *args], text=True).strip()


def source_id(path: str) -> str:
    return "SRC-" + hashlib.sha256(path.encode()).hexdigest()[:20]


def file_info(path: str, revision: str) -> tuple[bytes, dict]:
    content = subprocess.check_output(["git", "-C", str(ROOT), "show", f"{revision}:{path}"])
    blob = git("rev-parse", f"{revision}:{path}")
    item = {
        "sourceId": source_id(path), "sourceType": "git", "path": path,
        "uri": f"git:{revision}:{path}", "revision": revision,
        "contentHash": hashlib.sha256(content).hexdigest(),
        "captureMethod": f"git show {revision}:{path}; git blob {blob}",
    }
    return content, item


def read_rows(revision: str) -> tuple[list[dict], list[dict]]:
    trace = csv.DictReader(file_info(TRACE, revision)[0].decode("utf-8-sig").splitlines())
    requirements = list(trace)
    tasks = json.loads(file_info(DAG, revision)[0])['tasks']
    return requirements, tasks


def build(revision: str, generated_at: str | None = None) -> tuple[dict, dict]:
    revision = git("rev-parse", f"{revision}^{{commit}}")
    requirements, tasks = read_rows(revision)
    generated_at = generated_at or datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    sources = [file_info(path, revision)[1] for path in ALL_SOURCES]
    by_path = {item["path"]: item for item in sources}
    requirement_items = [{
        "id": row["requirement_id"], "title": row["title"], "priority": row["priority"],
        "behavior": row["behavior"], "acceptance": row["acceptance"],
        "source": row["source"], "status": row["status"],
        "implementationTasks": [part for part in row["implementation_tasks"].split(";") if part],
        "plannedTests": [part for part in row["planned_tests"].split(";") if part],
        "sourcePath": TRACE,
    } for row in requirements]
    task_items = [{
        "id": item["taskId"], "title": item["title"], "status": item["status"],
        "ownerRole": item["ownerRole"], "modules": item["modules"],
        "dependsOn": item["dependsOn"], "implementation": item["implementation"],
        "acceptance": item["acceptance"], "requirementIds": item["requirementIds"],
        "testIds": item["testIds"], "sourcePath": DAG,
    } for item in tasks]

    feature_specs = [
        ("FEATURE-COGNITION", "统一项目认知", "PD-01", "业务与技术知识共享项目、身份、版本和证据边界。"),
        ("FEATURE-READING", "业务阅读与判断", "PD-08", "按目的阅读业务功能、规则、案例、实际观察和实现，不将观察误作规范。"),
        ("FEATURE-GOVERNANCE", "知识治理与发布", "PD-21", "导入形成候选，经差异、审核和发布后才成为权威基线。"),
        ("FEATURE-COLLABORATION", "任务、影响与交付", "PD-25", "围绕目标、保留约束、影响、验证和知识交付追踪真实工作。"),
        ("FEATURE-SELF", "Self Project 自举", "PD-33", "以真实文档、代码、PR、测试和部署证据维护产品自身知识。"),
    ]
    features = [{
        "id": stable, "title": title, "requirementId": requirement,
        "summary": summary, "factKind": "documented_intent",
        "sourcePaths": [CHARTER, P1_README, V93_REVIEW, ENGINEERING],
    } for stable, title, requirement, summary in feature_specs]

    profile = {
        "name": "Business Atlas Self Project",
        "purpose": "管理软件项目知识，建立人与 AI 可共同引用、理解、判断和修订的项目认知空间。",
        "audience": "参与软件项目的业务负责人、产品/工程团队、审阅者和授权 Agent。",
        "boundaries": [
            "项目知识与权限隔离；需求、规范、观察、实现和证据保持不同语义。",
            "导入、审核、批准、发布和部署是不同事件；本候选不冒充批准基线。",
            "完整 T1.0 验收状态以 handoff 为准，目前尚未完成。",
        ],
        "sourcePaths": [CHARTER, P1_README, HANDOFF],
    }

    design = {
        "productEvolution": [
            {"title": "V9.2：项目工作空间与 Self Knowledge 原型", "sourcePath": V93_REVIEW, "factKind": "documented_intent"},
            {"title": "V9.3：业务阅读、规则案例和任务语义修订", "sourcePath": V93_REVIEW, "factKind": "documented_intent"},
        ],
        "releaseHistory": [
            {"title": "PR #78 合并", "sourcePath": PR_EVIDENCE, "factKind": "observed"},
            {"title": "Build 103 / DeployMac8 成功", "sourcePath": BUILD_EVIDENCE, "factKind": "observed"},
        ],
        "currentAcceptance": {"decision": "not_ready", "sourcePath": HANDOFF, "factKind": "observed"},
    }

    public_view = {
        "project": profile,
        "counts": {"requirements": len(requirement_items), "tasks": len(task_items), "features": len(features)},
        "requirements": requirement_items,
        "tasks": task_items,
        "features": features,
        "design": design,
        "documents": [{
            "path": path,
            "content": file_info(path, revision)[0].decode("utf-8-sig"),
            "contentHash": by_path[path]["contentHash"],
        } for path in PUBLIC_DOCUMENTS],
        "sources": [{"path": item["path"], "revision": revision, "contentHash": item["contentHash"], "uri": item["uri"]} for item in sources],
        "provenance": {"sourceRevision": revision, "generatedAt": generated_at, "sourceScope": "Repository docs/code and actual Azure DevOps records; no prototype simulation data."},
    }

    entities = [
        {
            "stableExternalId": "PROJECT-SELF", "typeId": "ProjectProfile", "name": profile["name"],
            "description": profile["purpose"], "properties": {
                "purpose": profile["purpose"], "audience": profile["audience"], "boundaries": profile["boundaries"],
                "blocks": [{"blockId": "self-purpose", "kind": "paragraph", "text": profile["purpose"], "references": []},
                           {"blockId": "self-boundaries", "kind": "list", "items": profile["boundaries"], "references": []}],
            }, "categoryAssignments": ["SelfProject"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "documented_intent", "sourceIds": [source_id(path) for path in profile["sourcePaths"]],
        }
    ]
    for feature in features:
        entities.append({
            "stableExternalId": feature["id"], "typeId": "Feature", "name": feature["title"],
            "description": feature["summary"], "properties": {
                "purpose": feature["summary"], "requirementId": feature["requirementId"], "factKind": feature["factKind"],
                "blocks": [{"blockId": feature["id"].lower(), "kind": "paragraph", "text": feature["summary"], "references": []}],
            }, "categoryAssignments": ["SelfProject", "Feature"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "documented_intent", "sourceIds": [source_id(path) for path in feature["sourcePaths"]],
        })
    for row in requirement_items:
        entities.append({
            "stableExternalId": row["id"], "typeId": "Requirement", "name": row["title"],
            "description": row["behavior"], "properties": {**row, "blocks": [
                {"blockId": row["id"].lower(), "kind": "paragraph", "text": row["behavior"], "references": []},
                {"blockId": row["id"].lower() + "-acceptance", "kind": "notice", "text": "验收意图：" + row["acceptance"], "references": []},
            ]}, "categoryAssignments": ["SelfProject", "Requirement"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "documented_intent", "sourceIds": [source_id(TRACE)],
        })
    for item in task_items:
        entities.append({
            "stableExternalId": item["id"], "typeId": "Task", "name": item["title"],
            "description": item["implementation"], "properties": {**item, "blocks": [
                {"blockId": item["id"].lower(), "kind": "paragraph", "text": item["implementation"], "references": []},
                {"blockId": item["id"].lower() + "-acceptance", "kind": "notice", "text": "基线验收意图：" + item["acceptance"], "references": []},
            ]}, "categoryAssignments": ["SelfProject", "Task"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "documented_intent", "sourceIds": [source_id(DAG)],
        })
    entities.extend([
        {"stableExternalId": "OBS-PR-78", "typeId": "Observation", "name": "PR #78 合并", "description": "Azure DevOps PR 78 merged into main as f95aef5.", "properties": json.loads(file_info(PR_EVIDENCE, revision)[0]), "categoryAssignments": ["SelfProject", "Engineering"], "applicableContext": {"project": "BusinessAtlas-Self"}, "verificationStatus": "observed", "sourceIds": [source_id(PR_EVIDENCE)]},
        {"stableExternalId": "OBS-BUILD-103", "typeId": "Observation", "name": "Build 103 与 Mac8 部署", "description": "Build and DeployMac8 succeeded for the merged main commit.", "properties": json.loads(file_info(BUILD_EVIDENCE, revision)[0]), "categoryAssignments": ["SelfProject", "Engineering"], "applicableContext": {"project": "BusinessAtlas-Self"}, "verificationStatus": "observed", "sourceIds": [source_id(BUILD_EVIDENCE)]},
        {"stableExternalId": "OBS-ACCEPTANCE", "typeId": "Observation", "name": "T1.0 验收状态", "description": "The repository handoff records not_ready and 154 requirements not_started; deployment health is not final acceptance.", "properties": {"decision": "not_ready", "requirementState": "not_started", "requirementCount": len(requirement_items)}, "categoryAssignments": ["SelfProject", "Engineering"], "applicableContext": {"project": "BusinessAtlas-Self"}, "verificationStatus": "observed", "sourceIds": [source_id(HANDOFF)]},
    ])

    package = {
        "manifest": {
            "formatVersion": "AKP/0.1", "packageId": "AKP-BUSINESS-ATLAS-SELF-2026-11",
            "sourceProjectId": "BusinessAtlas-Self", "projectName": profile["name"],
            "generatedAt": generated_at,
            "producer": {"kind": "tool", "identity": "scripts/self-project/build_case.py"},
            "sourceRevision": revision,
            "importPurpose": "Owner-requested Self Project case built from exact product design, source, PR and deployment evidence; documented intent remains distinct from observation and approval.",
            "declaredScope": ["project profile", "154 requirements", "44 task contracts", "design evolution", "implementation sources", "PR and deployment evidence"],
        },
        "entities": entities, "assertions": [], "businessStructures": {},
        "technicalStructures": {"codeRevision": revision, "architectureRevision": revision, "deploymentVersion": "Build 103"},
        "sources": sources,
        "unknownsAndConflicts": [{
            "id": "SELF-ACCEPTANCE-NOT-READY", "kind": "question", "title": "T1.0 acceptance remains incomplete",
            "description": "The controlled handoff records 154 requirements as not_started and G7 not_started. PR/build/deployment completion does not establish full product acceptance.",
            "relatedExternalIds": ["PROJECT-SELF", "OBS-ACCEPTANCE"], "status": "open", "blocksFormal": False,
        }],
        "importProvenance": {
            "agentRunId": "self-project-source-build", "toolExecutionRefs": [f"git rev-parse {revision}", f"git ls-tree {revision}"],
            "sourceFileRevisions": {path: revision for path in ALL_SOURCES},
            "extractedAt": generated_at,
            "extractionStrategy": "Exact Git object content, SHA-256 source hashes, structured CSV/JSON parsing, and captured Azure DevOps PR/build records.",
        },
    }
    return public_view, package


def write(path: Path, document: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default="HEAD")
    parser.add_argument("--generated-at")
    parser.add_argument("--public-output", type=Path, default=ROOT / "apps/web/src/features/public/self-project.json")
    parser.add_argument("--candidate-output", type=Path, default=ROOT / "knowledge/candidates/AKP-BUSINESS-ATLAS-SELF-2026-11.json")
    args = parser.parse_args()
    public_view, package = build(args.revision, args.generated_at)
    write(args.public_output, public_view)
    write(args.candidate_output, package)
    print(f"Built Self Project case: {len(public_view['requirements'])} requirements, {len(public_view['tasks'])} tasks, {len(package['entities'])} candidate entities")


if __name__ == "__main__":
    main()
