terraform {
  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.111.1"
    }
    talos = {
      source  = "siderolabs/talos"
      version = "~> 0.6.0"
    }
  }
}

provider "proxmox" {
  endpoint  = var.proxmox_connection.endpoint
  insecure  = try(var.proxmox_connection.insecure, true)
  api_token = try(trimspace(var.proxmox_connection.api_token), null) != "" ? var.proxmox_connection.api_token : null
  username  = try(trimspace(var.proxmox_connection.username), null) != "" ? var.proxmox_connection.username : null
  password  = try(trimspace(var.proxmox_connection.password), null) != "" ? var.proxmox_connection.password : null

  ssh {
    agent    = true
    username = "root"
    password = try(trimspace(var.proxmox_connection.password), null) != "" ? var.proxmox_connection.password : null
  }
}

/* --------------------------------------------------------------
*  Talos ISO Download
*  -------------------------------------------------------------- */
resource "proxmox_download_file" "talos_iso" {
  content_type        = "iso"
  datastore_id        = "local"
  node_name           = var.vm_defaults.node_name
  url                 = "https://factory.talos.dev/image/ce4c980550dd2ab1b17bbf2b08801c7eb59418eafe8f279833297925d67c7515/v1.7.6/metal-amd64.iso"
  file_name           = "talos-v1.7.6-qemu-guest-agent.iso"
  overwrite_unmanaged = true
}

/* --------------------------------------------------------------
*  Talos Master VMs
*  -------------------------------------------------------------- */
resource "proxmox_virtual_environment_vm" "master" {
  count     = 3
  name      = "ctf-master-${count.index + 1}"
  node_name = var.vm_defaults.node_name
  vm_id     = 600 + count.index

  tags = concat(["talos", "master", "ctf"], var.vm_defaults.tags)

  agent {
    enabled = true
  }

  machine = "q35"
  bios    = "ovmf"

  cpu {
    sockets = 1
    cores   = 2
    type    = "x86-64-v2"
  }

  memory {
    dedicated = 4096
  }

  cdrom {
    file_id   = proxmox_download_file.talos_iso.id
    interface = "ide0"
  }

  disk {
    datastore_id = "sas-disks" # Adjust based on user's datastore if needed, usually local-lvm or local-zfs. Variables says 'sas-disks' in previous
    interface    = "scsi0"
    size         = 20
    ssd          = true
    file_format  = "raw"
  }

  efi_disk {
    datastore_id = "sas-disks"
    file_format  = "raw"
    type         = "4m"
  }

  network_device {
    bridge  = var.vnet_name
    vlan_id = var.vlan_id
  }

  operating_system {
    type = "l26" # Linux
  }

  initialization {
    datastore_id = "sas-disks"
    ip_config {
      ipv4 {
        address = "dhcp"
      }
    }
  }
  
  boot_order = ["ide0", "scsi0"]
}

/* --------------------------------------------------------------
*  Talos Worker VMs
*  -------------------------------------------------------------- */
resource "proxmox_virtual_environment_vm" "worker" {
  count     = 6 # 6 workers
  name      = "ctf-worker-${count.index + 1}"
  node_name = var.vm_defaults.node_name
  vm_id     = 610 + count.index

  tags = concat(["talos", "worker", "ctf"], var.vm_defaults.tags)

  agent {
    enabled = true
  }

  machine = "q35"
  bios    = "ovmf"

  cpu {
    sockets = 1
    cores   = 4
    type    = "x86-64-v2"
  }

  memory {
    dedicated = 8192
  }

  cdrom {
    file_id   = proxmox_download_file.talos_iso.id
    interface = "ide0"
  }

  disk {
    datastore_id = "sas-disks"
    interface    = "scsi0"
    size         = 40
    ssd          = true
    file_format  = "raw"
  }

  efi_disk {
    datastore_id = "sas-disks"
    file_format  = "raw"
    type         = "4m"
  }

  network_device {
    bridge  = var.vnet_name
    vlan_id = var.vlan_id
  }

  operating_system {
    type = "l26"
  }

  initialization {
    datastore_id = "sas-disks"
    ip_config {
      ipv4 {
        address = "dhcp"
      }
    }
  }

  boot_order = ["ide0", "scsi0"]
}

/* --------------------------------------------------------------
*  Talos Provider Configuration
*  -------------------------------------------------------------- */

resource "talos_machine_secrets" "this" {}

data "talos_client_configuration" "this" {
  cluster_name         = "ctf-cluster"
  client_configuration = talos_machine_secrets.this.client_configuration
  endpoints            = [for vm in proxmox_virtual_environment_vm.master : vm.ipv4_addresses[1][0]]
}

data "talos_machine_configuration" "controlplane" {
  cluster_name     = "ctf-cluster"
  cluster_endpoint = "https://${proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]}:6443"
  machine_type     = "controlplane"
  machine_secrets  = talos_machine_secrets.this.machine_secrets
  config_patches = [
    yamlencode({
      machine = {
        install = {
          extensions = [
            {
              image = "ghcr.io/siderolabs/qemu-guest-agent:v8.2.0"
            }
          ]
        }
      }
    })
  ]
}

data "talos_machine_configuration" "worker" {
  cluster_name     = "ctf-cluster"
  cluster_endpoint = "https://${proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]}:6443"
  machine_type     = "worker"
  machine_secrets  = talos_machine_secrets.this.machine_secrets
  config_patches = [
    yamlencode({
      machine = {
        install = {
          extensions = [
            {
              image = "ghcr.io/siderolabs/qemu-guest-agent:v8.2.0"
            }
          ]
        }
      }
    })
  ]
}

resource "talos_machine_configuration_apply" "controlplane" {
  count                       = 3
  client_configuration        = talos_machine_secrets.this.client_configuration
  machine_configuration_input = data.talos_machine_configuration.controlplane.machine_configuration
  endpoint                    = proxmox_virtual_environment_vm.master[count.index].ipv4_addresses[1][0]
  node                        = proxmox_virtual_environment_vm.master[count.index].ipv4_addresses[1][0]
}

resource "talos_machine_configuration_apply" "worker" {
  count                       = 6
  client_configuration        = talos_machine_secrets.this.client_configuration
  machine_configuration_input = data.talos_machine_configuration.worker.machine_configuration
  endpoint                    = proxmox_virtual_environment_vm.worker[count.index].ipv4_addresses[1][0]
  node                        = proxmox_virtual_environment_vm.worker[count.index].ipv4_addresses[1][0]
}

resource "talos_machine_bootstrap" "this" {
  depends_on = [talos_machine_configuration_apply.controlplane]

  client_configuration = talos_machine_secrets.this.client_configuration
  endpoint             = proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]
  node                 = proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]
}

output "talosconfig" {
  value     = data.talos_client_configuration.this.talos_config
  sensitive = true
}

output "kubeconfig" {
  # You can use the talos_cluster_kubeconfig data source to fetch the kubeconfig,
  # but this requires the cluster to be fully bootstrapped.
  value = "Run 'talosctl kubeconfig --nodes ${proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]} --endpoints ${proxmox_virtual_environment_vm.master[0].ipv4_addresses[1][0]}' after bootstrap"
}
