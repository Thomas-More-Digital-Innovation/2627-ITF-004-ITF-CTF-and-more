/* --------------------------------------------------------------
*  OPNsense Provider Configuration (Central / Platform)
*  -------------------------------------------------------------- */
provider "opnsense" {
  uri            = var.opnsense.uri
  api_key        = var.opnsense.api_key
  api_secret     = var.opnsense.api_secret
  allow_insecure = try(var.opnsense.allow_insecure, true)
}

/* --------------------------------------------------------------
*  OPNsense VLAN Interfaces (one per environment managed by .NET)
*  -------------------------------------------------------------- */
resource "opnsense_interfaces_vlan" "env_vlan" {
  for_each    = var.environments
  parent      = var.opnsense.parent_interface
  tag         = each.value.vlan_id
  description = "VLAN ${each.value.vlan_id} for environment ${each.key}"
}

/* --------------------------------------------------------------
*  Firewall Filter Rules: Allow Environment /16 Internet Access
*  -------------------------------------------------------------- */
resource "opnsense_firewall_filter" "allow_env_internet" {
  for_each    = var.environments
  enabled     = true
  description = "Allow Internet access for ${each.key} (${each.value.cidr} on VLAN ${each.value.vlan_id})"
  sequence    = 100 + each.value.vlan_id

  interface = {
    interface = [
      opnsense_interfaces_vlan.env_vlan[each.key].device != "" ? opnsense_interfaces_vlan.env_vlan[each.key].device : "vlan0.${each.value.vlan_id}"
    ]
  }

  filter = {
    quick       = true
    action      = "pass"
    direction   = "in"
    protocol    = "any"
    ip_protocol = "inet"

    source = {
      net = each.value.cidr
    }

    destination = {
      net = "any"
    }
  }
}

/* --------------------------------------------------------------
*  Firewall Filter Rules: Allow Management to Environment
*  -------------------------------------------------------------- */
resource "opnsense_firewall_filter" "allow_mgmt_to_env" {
  for_each    = var.environments
  enabled     = true
  description = "Allow Management to ${each.key} (${each.value.cidr} on VLAN ${each.value.vlan_id})"
  sequence    = 200 + each.value.vlan_id

  interface = {
    # Floating rule to permit traffic from management to environment subnets
    interface = []
  }

  filter = {
    quick       = true
    action      = "pass"
    direction   = "in"
    protocol    = "any"
    ip_protocol = "inet"

    source = {
      net = "any"
    }

    destination = {
      net = each.value.cidr
    }
  }
}

