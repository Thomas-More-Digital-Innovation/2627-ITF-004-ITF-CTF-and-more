variable "proxmox_connection" {
  description = "Proxmox connection settings. Use either username/password or api_token."
  type = object({
    endpoint  = string
    username  = optional(string)
    password  = optional(string)
    api_token = optional(string)
  })

  validation {
    condition = (
      (var.proxmox_connection.api_token != null && trimspace(var.proxmox_connection.api_token) != "") ||
      (
        var.proxmox_connection.username != null && trimspace(var.proxmox_connection.username) != "" &&
        var.proxmox_connection.password != null && trimspace(var.proxmox_connection.password) != ""
      )
    )
    error_message = "Specify either a Proxmox api_token or both username and password."
  }
}

variable "vm_defaults" {
  description = "Default values for VMs"
  type = object({
    node_name              = string
    template_id            = optional(number)
    tags                   = list(string)
    provisioning_wait_time = optional(number, 0)
  })
}

variable "cloud_image" {
  description = "Cloud image configuration to download and use for VMs"
  type = object({
    url          = string
    file_name    = string
    datastore_id = optional(string, "truenas-isos")
  })
  default = {
    url          = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2"
    file_name    = "debian-12-generic-amd64.img"
    datastore_id = "truenas-isos"
  }
}