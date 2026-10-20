namespace Application.Interfaces
{
    public interface IOpnsenseService
    {
        /// <summary>
        /// Idempotently creates the VLAN, assigns it as an interface and adds the
        /// firewall rule(s) giving the environment internet (non-RFC1918) access.
        /// </summary>
        Task ConfigureEnvironmentNetworkAsync(int vlanId, string cidr, string environmentName, CancellationToken ct = default);
    }
}

