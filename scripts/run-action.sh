#!/usr/bin/env bash
# Runs PR Buddy for the GitHub Action: builds the CLI, writes the job summary,
# creates or updates one PR comment, then fails the job if --fail-on says so.
set -euo pipefail

if [[ "${GITHUB_EVENT_NAME}" != pull_request* ]]; then
  echo "PR Buddy only runs on pull_request events (got ${GITHUB_EVENT_NAME}). Skipping."
  exit 0
fi

# actions/checkout leaves HEAD on the PR's merge commit. Make sure the base branch is there too.
git fetch --no-tags --quiet origin "+refs/heads/${GITHUB_BASE_REF}:refs/remotes/origin/${GITHUB_BASE_REF}"

dotnet build "${GITHUB_ACTION_PATH}/src/PrBuddy.Cli" -c Release -nologo -v quiet
cli=(dotnet "${GITHUB_ACTION_PATH}/src/PrBuddy.Cli/bin/Release/net10.0/prbuddy.dll")

args=("origin/${GITHUB_BASE_REF}" "HEAD" --markdown --link-base "${LINK_BASE}")
[[ -n "${INPUT_CONFIG}" ]] && args+=(--config "${INPUT_CONFIG}")
[[ -n "${INPUT_FAIL_ON}" ]] && args+=(--fail-on "${INPUT_FAIL_ON}")

report="${RUNNER_TEMP}/prbuddy.md"
set +e
"${cli[@]}" "${args[@]}" > "${report}"
status=$?
set -e

if [[ ${status} -ge 2 ]]; then
  echo "::error::PR Buddy couldn't run (see the error above)."
  exit "${status}"
fi

cat "${report}" >> "${GITHUB_STEP_SUMMARY}"

if [[ "${INPUT_COMMENT}" == "true" ]]; then
  pr=$(jq -r '.pull_request.number' "${GITHUB_EVENT_PATH}")
  comments="repos/${GITHUB_REPOSITORY}/issues/${pr}/comments"
  # Update our earlier comment rather than adding a new one on every push.
  existing=$(gh api "${comments}" --paginate \
    --jq '.[] | select(.user.type == "Bot" and (.body | startswith("<!-- prbuddy -->"))) | .id') || existing=""
  existing="${existing%%$'\n'*}" # first match only

  if [[ -n "${existing}" ]]; then
    gh api -X PATCH "repos/${GITHUB_REPOSITORY}/issues/comments/${existing}" -F "body=@${report}" > /dev/null \
      || echo "::warning::Couldn't update the PR comment. Fork PRs get a read-only token; the report is in the job summary."
  else
    gh api -X POST "${comments}" -F "body=@${report}" > /dev/null \
      || echo "::warning::Couldn't post the PR comment. Fork PRs get a read-only token; the report is in the job summary."
  fi
fi

if [[ ${status} -eq 1 ]]; then
  echo "::error::PR Buddy found issues at or above the fail-on level."
fi
exit "${status}"
