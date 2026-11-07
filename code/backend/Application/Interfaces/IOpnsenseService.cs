namespace Application.Interfaces
{
    public interface IOpnsenseService
    {
        /// <summary>
        /// Idempotently creates the VLAN, assigns it as an interface and adds the
        /// firewall rule(s) giving the environment internet (non-RFC1918) access.
        /// </summary>
        Task ConfigureEnvironmentNetworkAsync(int vlanId, string cidr, string environmentName, CancellationToken ct = default);

        /// <summary>
        /// Removes the environment's firewall rule, interface assignment and VLAN.
        /// Missing items are skipped, so it is safe to call repeatedly. The shared RFC1918 alias is kept.
        /// </summary>
        Task RemoveEnvironmentNetworkAsync(int vlanId, string environmentName, CancellationToken ct = default);
    }
}

