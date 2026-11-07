# DOCK - Deployable On-premise Cyber Kit

The dock is a portable on premise toolkit for any cyber-related environments. The project is a physical flight case containing 3 nodes, 2 switches and a couple APs. The repository is structured as follows:

code - contains all code for the platform

-- backend contains a .NET 10 API used to deploy and manage the environments
----------------------------------------------------------------------------

-- frontend contains a Vue.js SPA as an interface to the API

-- terraform contains the platform (management) IaC. Environments are deployed by the backend via inline C# Pulumi programs.


documentation - contains all documentation of meetings, diagrams, etc.
