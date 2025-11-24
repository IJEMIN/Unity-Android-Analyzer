#!/usr/bin/env bash
set -euo pipefail

# ========= Config =========

# adb wrapper (later we may inject -s <serial>)
ADB_BIN="adb"

# ========= Utility =========

log() {
  # progress messages to stderr (stdout is reserved for Markdown)
  echo "$@" >&2
}

check_deps() {
  for cmd in "$ADB_BIN" unzip strings grep sed awk sort uniq head; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
      echo "Error: '$cmd' not found in PATH" >&2
      exit 1
    fi
  done
}

select_device_if_needed() {
  local devices
  devices=$($ADB_BIN devices | awk 'NR>1 && $2=="device"{print $1}' || true)

  if [[ -z "$devices" ]]; then
    echo "Error: No connected Android device found." >&2
    exit 1
  fi

  local count
  count=$(echo "$devices" | wc -l | tr -d ' ')

  if [[ "$count" -eq 1 ]]; then
    local serial
    serial=$(echo "$devices" | head -n1)
    log "[*] Using device: $serial"
    ADB_BIN="adb -s $serial"
  else
    log "[*] Multiple devices found:"
    local i=1
    while IFS= read -r d; do
      log "  [$i] $d"
      i=$((i+1))
    done <<< "$devices"

    local choice
    read -r -p "Select device number: " choice
    if ! [[ "$choice" =~ ^[0-9]+$ ]]; then
      echo "Invalid choice." >&2
      exit 1
    fi
    local sel
    sel=$(echo "$devices" | sed -n "${choice}p" || true)
    if [[ -z "$sel" ]]; then
      echo "Invalid choice." >&2
      exit 1
    fi
    log "[*] Using device: $sel"
    ADB_BIN="adb -s $sel"
  fi
}

# ========= Analysis helpers (APK/OBB common) =========

# Search all given containers (APK/OBB) for various Unity-related artifacts.

detect_unity_version_static_from_containers() {
  local containers=("$@")
  local version_pattern="20[0-9]{2}\.[0-9]+\.[0-9]+[a-z][0-9]*"

  # 1) globalgamemanagers
  for c in "${containers[@]}"; do
    if unzip -l "$c" 2>/dev/null | grep -q "assets/bin/Data/globalgamemanagers"; then
      log "[*] Searching Unity version in assets/bin/Data/globalgamemanagers from $(basename "$c")"
      unzip -p "$c" assets/bin/Data/globalgamemanagers > _globalgm 2>/dev/null || true
      local v
      v=$(strings _globalgm | grep -E "$version_pattern" | head -n 1 || true)
      if [[ -n "$v" ]]; then
        echo "$v" | awk '{print $1}'
        return 0
      fi
    fi
  done

  # 2) data.unity3d
  for c in "${containers[@]}"; do
    if unzip -l "$c" 2>/dev/null | grep -q "assets/bin/Data/data.unity3d"; then
      log "[*] Searching Unity version in assets/bin/Data/data.unity3d from $(basename "$c")"
      unzip -p "$c" assets/bin/Data/data.unity3d > _dataunity 2>/dev/null || true
      local v
      v=$(strings _dataunity | grep -E "$version_pattern" | head -n 1 || true)
      if [[ -n "$v" ]]; then
        echo "$v" | awk '{print $1}'
        return 0
      fi
    fi
  done

  # 3) libunity.so (arm64, v7a)
  for libpath in "lib/arm64-v8a/libunity.so" "lib/armeabi-v7a/libunity.so"; do
    for c in "${containers[@]}"; do
      if unzip -l "$c" 2>/dev/null | grep -q "$libpath"; then
        log "[*] Searching Unity version in $libpath from $(basename "$c")"
        unzip -p "$c" "$libpath" > _libunity 2>/dev/null || true
        local v
        v=$(strings _libunity | grep -E "$version_pattern" | head -n 1 || true)
        if [[ -n "$v" ]]; then
          echo "$v" | awk '{print $1}'
          return 0
        fi
      fi
    done
  done

  # 4) Fallback: global-metadata.dat (rarely needed)
  local metadata_file
  metadata_file=$(extract_global_metadata_from_containers "${containers[@]}" 2>/dev/null || echo "")
  if [[ -n "$metadata_file" && -f "$metadata_file" ]]; then
    local v
    v=$(strings "$metadata_file" | grep -E "$version_pattern" | head -n 1 || true)
    if [[ -n "$v" ]]; then
      echo "$v" | awk '{print $1}'
      return 0
    fi
  fi

  echo ""
  return 1
}

extract_global_metadata_from_containers() {
  local containers=("$@")
  local tmp_file="global-metadata.dat"

  rm -f "$tmp_file"
  for c in "${containers[@]}"; do
    if unzip -l "$c" 2>/dev/null | grep -q "assets/bin/Data/Managed/Metadata/global-metadata.dat"; then
      log "[*] Found global-metadata.dat in $(basename "$c")"
      unzip -p "$c" assets/bin/Data/Managed/Metadata/global-metadata.dat > "$tmp_file" 2>/dev/null || true
      if [[ -s "$tmp_file" ]]; then
        echo "$tmp_file"
        return 0
      fi
    fi
  done

  echo ""
  return 1
}

detect_render_pipeline() {
  local metadata_file="$1"

  if [[ -z "$metadata_file" || ! -f "$metadata_file" ]]; then
    echo "Unknown"
    return
  fi

  local has_urp has_hdrp
  has_urp=$(strings "$metadata_file" | grep -i -E "UnityEngine\.Rendering\.Universal|UniversalRenderPipeline|ForwardRenderer|Renderer2D" || true)
  has_hdrp=$(strings "$metadata_file" | grep -i -E "UnityEngine\.Rendering\.HighDefinition|HDRenderPipeline" || true)

  if [[ -n "$has_urp" ]]; then
    echo "URP"
  elif [[ -n "$has_hdrp" ]]; then
    echo "HDRP"
  else
    echo "Built-in"
  fi
}

detect_entities() {
  local metadata_file="$1"

  if [[ -z "$metadata_file" || ! -f "$metadata_file" ]]; then
    echo "Unknown"
    return
  fi

  local has_entities
  has_entities=$(strings "$metadata_file" | grep -i -E "Unity\.Entities|EntityManager|EntityComponentStore|ComponentSystemGroup|HybridRenderer|Unity\.Physics" || true)

  if [[ -n "$has_entities" ]]; then
    echo "yes"
  else
    echo "no"
  fi
}

detect_addressables_from_containers() {
  local containers=("$@")
  local has_addr=""

  for c in "${containers[@]}"; do
    if unzip -l "$c" 2>/dev/null | grep -iq -E "aa/|addressables|catalog.*\.json|catalog.*\.hash"; then
      has_addr="yes"
      break
    fi
  done

  if [[ -z "$has_addr" ]]; then
    echo "no"
  else
    echo "yes"
  fi
}

detect_major_namespaces() {
  local metadata_file="$1"

  if [[ -z "$metadata_file" || ! -f "$metadata_file" ]]; then
    echo ""
    return
  fi

  # Extract likely type names, approximate namespace = first.two segments.
  # Output: "count namespace" sorted by count desc, top 20.
  strings "$metadata_file" \
    | grep -E '^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z0-9_.]+' \
    | awk -F'.' '{
        if (NF>=2) {
          print $1"."$2
        } else {
          print $1
        }
      }' \
    | sort \
    | uniq -c \
    | sort -nr \
    | head -n 20
}

print_markdown_report() {
  local title="$1"; shift
  local containers=("$@")

  local unity_version
  unity_version=$(detect_unity_version_static_from_containers "${containers[@]}")
  [[ -z "$unity_version" ]] && unity_version="Unknown"

  local metadata_file
  metadata_file=$(extract_global_metadata_from_containers "${containers[@]}" || echo "")

  local render_pipeline
  render_pipeline=$(detect_render_pipeline "$metadata_file")

  local entities_used
  entities_used=$(detect_entities "$metadata_file")

  local addr_used
  addr_used=$(detect_addressables_from_containers "${containers[@]}")

  local ns_list
  ns_list=$(detect_major_namespaces "$metadata_file")

  echo "## $title"
  echo
  echo "- **Unity Version:** \`$unity_version\`"
  echo "- **Render Pipeline:** \`$render_pipeline\`"
  echo "- **Entities Used:** \`$entities_used\`"
  echo "- **Addressables Used:** \`$addr_used\`"
  echo
  echo "### Major Namespaces (top 20)"
  echo

  if [[ -z "$ns_list" ]]; then
    echo "_No namespace information available (metadata not found or unreadable)._"
  else
    while IFS= read -r line; do
      [[ -z "$line" ]] && continue
      local count ns
      count=$(echo "$line" | awk '{print $1}')
      ns=$(echo "$line" | sed -E 's/^[[:space:]]*[0-9]+[[:space:]]+//')
      echo "- \`$ns\` ($count)"
    done <<< "$ns_list"
  fi

  echo
  echo "---"
  echo
}

# ========= Mode 1: Local APK/OBB =========

local_mode() {
  while true; do
    echo >&2
    read -r -p "Enter path to APK file (or press Enter to finish): " apk_path
    if [[ -z "${apk_path}" ]]; then
      log "[*] Local analysis finished."
      break
    fi

    if [[ ! -f "$apk_path" ]]; then
      log "[!] File not found: $apk_path"
      continue
    fi

    local containers=("$apk_path")

    # Ask about OBBs
    local ans
    read -r -p "Does this app have OBB expansion files? (y/n): " ans
    ans=${ans:-n}

    if [[ "$ans" == "y" || "$ans" == "Y" ]]; then
      while true; do
        read -r -p "Enter path to an OBB file (or press Enter if there are no more): " obb_path
        if [[ -z "${obb_path}" ]]; then
          break
        fi
        if [[ ! -f "$obb_path" ]]; then
          log "[!] OBB file not found: $obb_path"
          continue
        fi
        containers+=("$obb_path")
      done
    fi

    local title
    title="Local: $(basename "$apk_path")"
    print_markdown_report "$title" "${containers[@]}"
  done
}

# ========= Mode 2: Device APK/OBB =========

device_mode() {
  select_device_if_needed

  local keyword
  read -r -p "Enter keyword to search in package name or app label: " keyword
  if [[ -z "$keyword" ]]; then
    echo "Keyword is empty." >&2
    return
  fi

  log "[*] Searching packages matching: $keyword"

  local pkgs
  pkgs=$($ADB_BIN shell pm list packages | sed 's/^package://g' | grep -i "$keyword" || true)

  if [[ -z "$pkgs" ]]; then
    echo "No packages found for keyword '$keyword'." >&2
    return
  fi

  local indexed_pkgs=()
  local i=1

  while IFS= read -r p; do
    [[ -z "$p" ]] && continue
    local label
    label=$($ADB_BIN shell dumpsys package "$p" 2>/dev/null | grep -m 1 "application-label:" | sed 's/.*application-label://g' || true)
    if [[ -z "$label" ]]; then
      log "  [$i] $p"
    else
      log "  [$i] $p  (label: $label)"
    fi
    indexed_pkgs+=("$p")
    i=$((i+1))
  done <<< "$pkgs"

  if [[ "${#indexed_pkgs[@]}" -eq 0 ]]; then
    echo "No packages parsed." >&2
    return
  fi

  local choice
  read -r -p "Select package number: " choice
  if ! [[ "$choice" =~ ^[0-9]+$ ]]; then
    echo "Invalid choice." >&2
    return
  fi

  local idx=$((choice - 1))
  if (( idx < 0 || idx >= ${#indexed_pkgs[@]} )); then
    echo "Invalid choice." >&2
    return
  fi

  local pkg="${indexed_pkgs[$idx]}"
  log "[*] Selected package: $pkg"

  # Work dir
  local workdir
  workdir=$(mktemp -d "unity_app_${pkg//./_}_XXXX")
  log "[*] Working directory: $workdir"
  pushd "$workdir" >/dev/null

  # Pull APKs
  local paths
  paths=$($ADB_BIN shell pm path "$pkg" 2>/dev/null | sed 's/package://g' || true)
  if [[ -z "$paths" ]]; then
    echo "No APK paths found for $pkg." >&2
    popd >/dev/null
    rm -rf "$workdir"
    return
  fi

  local containers=()
  while IFS= read -r p; do
    [[ -z "$p" ]] && continue
    local local_name
    local_name=$(basename "$p")
    log "[*] Pulling APK: $p -> $local_name"
    $ADB_BIN pull "$p" "$local_name" >/dev/null
    containers+=("$local_name")
  done <<< "$paths"

  # Pull OBBs if exist: /sdcard/Android/obb/<pkg>/*
  local obb_list
  obb_list=$($ADB_BIN shell ls "/sdcard/Android/obb/$pkg" 2>/dev/null || true)
  if [[ -n "$obb_list" ]]; then
    log "[*] Found OBB directory on device: /sdcard/Android/obb/$pkg"
    while IFS= read -r f; do
      [[ -z "$f" ]] && continue
      # Some devices prepend path; normalize
      if [[ "$f" != /* ]]; then
        f="/sdcard/Android/obb/$pkg/$f"
      fi
      local obb_local
      obb_local=$(basename "$f")
      log "[*] Pulling OBB: $f -> $obb_local"
      $ADB_BIN pull "$f" "$obb_local" >/dev/null
      containers+=("$obb_local")
    done <<< "$obb_list"
  else
    log "[*] No OBB directory found for $pkg (this is fine)."
  fi

  local title
  title="Device: $pkg"
  print_markdown_report "$title" "${containers[@]}"

  popd >/dev/null
  rm -rf "$workdir"
}

# ========= Main =========

main() {
  check_deps

  echo "# Unity App Analysis"
  echo

  echo "Select mode:"
  echo "  [1] Analyze existing local APK/OBB files"
  echo "  [2] Extract APK/OBB from connected Android device"
  echo "  [q] Quit"
  echo

  read -r -p "Enter choice: " mode

  case "$mode" in
    1)
      local_mode
      ;;
    2)
      device_mode
      ;;
    q|Q)
      exit 0
      ;;
    *)
      echo "Invalid choice." >&2
      exit 1
      ;;
  esac
}

main "$@"