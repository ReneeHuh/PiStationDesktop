# PiStation pull-request review plan

## Scope

Pi-only, local Windows desktop. Implement GitHub PR review inside the application: details/body, commits/checks, hosted file diffs, inline review drafts, review submission, thread replies and resolve/reopen. Existing GitLab/Azure list/create/mutation flows remain; the new review workspace is explicitly capability-gated to GitHub. Other agent runtimes, remotes/non-Windows clients, auto-merge, checkout/worktree creation and additional hosting adapters are excluded from this milestone.

## Implementation sequence

1. Define bounded review snapshots, diff lines, repository/head-bound write requests and protocol serialization. Keep operation identities and existing uncertain-write recovery.
2. Luna host task: GitHub reads and writes through the authenticated CLI, explicit host/repository routing, current-head and inline-line validation, thread ownership/permission checks, bounded responses and provider fixture tests.
3. Luna desktop task: native PR review workspace with details/checks/commits/files, selectable diff lines, staged inline drafts, replies, resolve/reopen, review submission, loading/error states and capability gates. Preserve drafts and prevent stale asynchronous results from crossing PR/project selections.
4. Main agent: host/hub/client wiring, persistent local review drafts, protocol bump, end-to-end fixture coverage, and independent review/correction of Luna changes.
5. Verify clean Windows build, provider/parsing/validation tests, host/client/protocol regression tests, visual contract and native UI acceptance where tooling permits. No authenticated external writes during development validation.

## Acceptance

- A selected GitHub PR displays authoritative metadata, checks, commits, comments and hosted patches, including renamed/binary/omitted patches and explicit truncation notices.
- Only real diff lines can receive inline drafts; submissions bind to the viewed repository and head commit. Changed heads require reload and draft review.
- Review drafts survive closing/reopening and app restart, scoped by project/repository/PR/head. Drafts clear only after confirmed submission.
- Replies and resolve/reopen operate on threads belonging to the selected PR and honor provider permissions.
- Every write uses a unique operation identity; a lost response preserves uncertain status and never automatically retries with a new identity.
- Pi setup, planning, sessions and subagents retain their current behavior.

## Review and handoff

The primary agent reviews all delegated changes, corrects findings and records actual test results and provider limits before completion. The feature matrix is not automatically re-rated.

Implementation and review are recorded in [the milestone handoff](PI-PR-REVIEW-2026-09-06.md). Authenticated provider and native visual acceptance remain explicit follow-ups, not inferred from fixture tests.
