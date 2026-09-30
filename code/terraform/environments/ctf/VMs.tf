terraform {
  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.111.1"
    }
  }
}

provider "proxmox" {
  endpoint  = var.proxmox_connection.endpoint
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
*  Cloud-Init Snippet: installs and starts qemu-guest-agent on boot
*  -------------------------------------------------------------- */
resource "proxmox_virtual_environment_file" "cloud_init_vendor" {
  content_type = "snippets"
  datastore_id = var.cloud_image.datastore_id
  node_name    = var.vm_defaults.node_name

  source_raw {
    data = <<-EOF
      #cloud-config
      package_update: true
      packages:
        - qemu-guest-agent
      runcmd:
        - systemctl enable --now qemu-guest-agent
    EOF

    file_name = "qemu-guest-agent.yaml"
  }
}

/* --------------------------------------------------------------
*  Cloud-Image Download
*  -------------------------------------------------------------- */
resource "proxmox_download_file" "cloud_image" {
  content_type = "iso"
  datastore_id = var.cloud_image.datastore_id
  node_name    = var.vm_defaults.node_name
  url          = var.cloud_image.url
  file_name    = var.cloud_image.file_name
}

/* --------------------------------------------------------------
*  TEST-VM
*  -------------------------------------------------------------- */
resource "proxmox_virtual_environment_vm" "TEST-VM-VAN-DAVID-ctf" {
  name      = "TEST-VM-VAN-DAVID-ctf"
  node_name = var.vm_defaults.node_name
  vm_id     = 610
  migrate   = true

  tags = concat(["prod"], var.vm_defaults.tags)

  boot_order    = ["scsi0"]
  scsi_hardware = "virtio-scsi-single"

  agent {
    enabled = true
    timeout = "15m"
  }

  provisioner "local-exec" {
    command = "sleep ${var.vm_defaults.provisioning_wait_time}"
  }

  machine = "q35"

  bios = "ovmf"

  cpu {
    sockets = 1
    cores   = 4
    type    = "x86-64-v2"
  }

  memory {
    dedicated = 8192
  }

  disk {
    datastore_id = "local-vm"
    file_id      = proxmox_download_file.cloud_image.id
    interface    = "scsi0"
    size         = 16
    ssd          = true
  }

  efi_disk {
    datastore_id      = "local-vm"
    file_format       = "raw"
    type              = "4m"
    pre_enrolled_keys = true
  }

  network_device {
    bridge = "vmbr0" // SDN defined network
  }

  serial_device {
    device = "socket"
  }

  initialization {
    datastore_id = "local-vm"

    vendor_data_file_id = proxmox_virtual_environment_file.cloud_init_vendor.id

    user_account {
      username = "root"
      keys = [
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICZsbZIVfLuGRGQyNVkdh11rsl+YZBEgNVe9WoiR15R2"
      ]
    }

    dns {
      servers = ["1.1.1.1"]
    }

    ip_config {
      ipv4 {
        address = "dhcp"
      }
    }
  }
}