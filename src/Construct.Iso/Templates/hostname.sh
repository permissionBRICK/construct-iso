#!/bin/sh
#
# Construct: adopt this VM's identity from the hypervisor's own channel.
#
# The install media is GENERIC -- it carries the placeholder hostname
# 'construct-seed' -- and every VM built from it learns its real name here, at first
# boot. That name matters beyond cosmetics: the guest registers it with the virtual
# switch's DHCP/DNS, and the host (and the host service's port forwards) reach the VM
# as <name>.mshome.net, so the guest hostname MUST equal the VM name.
#
# The source is pluggable: CONSTRUCT_HOSTNAME_SOURCE (set at build time through
# VM_HOSTNAME_SOURCE, delivered as /etc/default/construct-hostname) selects it.
#   hyperv-kvp           Hyper-V data exchange -- today.
#   cloud-init-metadata  planned: Proxmox / NoCloud / ConfigDrive, where the
#                        hypervisor supplies per-VM identity natively.
# Adding one means adding a case to read_identity() and nothing else.
set -eu

CONSTRUCT_HOSTNAME_SOURCE="${CONSTRUCT_HOSTNAME_SOURCE:-hyperv-kvp}"
CONSTRUCT_KVP_POOL="${CONSTRUCT_KVP_POOL:-/var/lib/hyperv/.kvp_pool_3}"
CONSTRUCT_HOSTNAME_WAIT="${CONSTRUCT_HOSTNAME_WAIT:-180}"
CONSTRUCT_MARKER_DIR="${CONSTRUCT_MARKER_DIR:-/var/lib/construct}"
CONSTRUCT_MARKER="${CONSTRUCT_MARKER_DIR}/hostname-adopted"

log() { echo "construct-hostname: $*"; }

# --- source: Hyper-V KVP -----------------------------------------------------
# Pool 3 is the host-to-guest "intrinsic" pool hv_kvp_daemon writes (package
# linux-cloud-tools-virtual). It is a flat file of fixed-size records: a 512-byte
# key followed by a 2048-byte value, both NUL-padded. 2560 = 5 * 512, so every half
# starts on a 512-byte boundary and dd can seek to it in whole blocks.
kvp_lookup() {
  _key="$1"
  [ -s "${CONSTRUCT_KVP_POOL}" ] || return 1

  _size="$(wc -c <"${CONSTRUCT_KVP_POOL}")"
  _records=$(( _size / 2560 ))
  _i=0
  while [ "${_i}" -lt "${_records}" ]; do
    _name="$(dd if="${CONSTRUCT_KVP_POOL}" bs=512 skip=$(( _i * 5 )) count=1 2>/dev/null | tr -d '\000')"
    if [ "${_name}" = "${_key}" ]; then
      dd if="${CONSTRUCT_KVP_POOL}" bs=512 skip=$(( _i * 5 + 1 )) count=4 2>/dev/null | tr -d '\000'
      return 0
    fi
    _i=$(( _i + 1 ))
  done
  return 1
}

# The one place that knows about sources. Echoes the raw name, or fails.
read_identity() {
  case "${CONSTRUCT_HOSTNAME_SOURCE}" in
    hyperv-kvp)
      kvp_lookup VirtualMachineName
      ;;
    *)
      log "unknown identity source '${CONSTRUCT_HOSTNAME_SOURCE}'"
      return 1
      ;;
  esac
}

# --- name -> hostname --------------------------------------------------------
normalize_name() {
  printf '%s' "$1" | tr -d '\000\r\n' | tr 'A-Z' 'a-z'
}

# A DNS label, because that is what the switch's DNS will publish. Anything else
# (a VM named "Build Box (2)") is refused rather than half-applied.
valid_label() {
  printf '%s' "$1" | grep -Eq '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'
}

apply_hostname() {
  _name="$1"

  if command -v hostnamectl >/dev/null 2>&1; then
    hostnamectl set-hostname "${_name}"
  else
    printf '%s\n' "${_name}" >/etc/hostname
    hostname "${_name}"
  fi

  # 127.0.1.1 is how Debian/Ubuntu resolve the machine's own name; leaving the
  # placeholder there makes every self-lookup (sudo included) wait for a timeout.
  if grep -q '^127\.0\.1\.1' /etc/hosts 2>/dev/null; then
    sed -i "s/^127\.0\.1\.1.*/127.0.1.1\t${_name}/" /etc/hosts
  else
    printf '127.0.1.1\t%s\n' "${_name}" >>/etc/hosts
  fi
}

# The name only becomes REACHABLE once the DHCP server has seen it: the lease
# carries the hostname option, and that is what the switch's DNS publishes.
renew_dhcp() {
  _renewed=1

  if command -v networkctl >/dev/null 2>&1; then
    for _link in $(networkctl list --no-legend 2>/dev/null | awk '$3 == "ether" { print $2 }'); do
      # A RENEW (DHCPREQUEST) is not enough for Hyper-V's Default Switch resolver, which
      # keeps the name it learnt with the first lease; a reconfigure restarts the client
      # (DISCOVER with the new hostname), which is what makes <name>.mshome.net appear
      # (field, 2026-09-04). renew stays as the fallback for older networkctl.
      if networkctl reconfigure "${_link}" >/dev/null 2>&1; then _renewed=0
      elif networkctl renew "${_link}" >/dev/null 2>&1; then _renewed=0; fi
    done
  fi

  if [ "${_renewed}" -ne 0 ] && command -v netplan >/dev/null 2>&1; then
    if netplan apply >/dev/null 2>&1; then _renewed=0; fi
  fi

  if [ "${_renewed}" -ne 0 ]; then
    log "could not renew the DHCP lease; the name may take until the next renewal to resolve"
  fi
}

main() {
  if [ -f "${CONSTRUCT_MARKER}" ]; then
    log "already adopted: $(cat "${CONSTRUCT_MARKER}")"
    return 0
  fi

  # Bounded: the KVP daemon comes up on its own schedule, and a VM that never gets a
  # name must still finish booting (with the placeholder) instead of blocking.
  _waited=0
  _name=""
  while : ; do
    _raw="$(read_identity 2>/dev/null || true)"
    _name="$(normalize_name "${_raw}")"
    if [ -n "${_name}" ] && valid_label "${_name}"; then
      break
    fi
    if [ "${_waited}" -ge "${CONSTRUCT_HOSTNAME_WAIT}" ]; then
      log "no usable name from '${CONSTRUCT_HOSTNAME_SOURCE}' after ${CONSTRUCT_HOSTNAME_WAIT}s; keeping $(hostname)"
      return 0
    fi
    sleep 2
    _waited=$(( _waited + 2 ))
  done

  if [ "${_name}" = "$(hostname)" ]; then
    log "hostname is already ${_name}"
  else
    log "adopting hostname '${_name}' from ${CONSTRUCT_HOSTNAME_SOURCE}"
    apply_hostname "${_name}"
    renew_dhcp
  fi

  mkdir -p "${CONSTRUCT_MARKER_DIR}"
  printf '%s\n' "${_name}" >"${CONSTRUCT_MARKER}"
  systemctl disable construct-hostname.service >/dev/null 2>&1 || true
  log "done"
}

# Sourced with CONSTRUCT_HOSTNAME_LIB=1 the functions above stand on their own --
# that is how test/autoinstall-iso.test.sh exercises the pool parser against a
# synthetic .kvp_pool_3 without booting anything.
[ "${CONSTRUCT_HOSTNAME_LIB:-0}" = "1" ] || main "$@"
