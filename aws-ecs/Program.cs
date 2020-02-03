using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Pulumi;
using Ec2 = Pulumi.Aws.Ec2;
using Ecs = Pulumi.Aws.Ecs;
using Ecr = Pulumi.Aws.Ecr;
using Elb = Pulumi.Aws.ElasticLoadBalancingV2;
using Iam = Pulumi.Aws.Iam;
using Pulumi.Aws.DynamoDB.Inputs;
using Pulumi.Aws.AutoScaling;

class Program
{
    private static string baseName = "pulumi-ecs-test";
    static Output<string> vpcId = null;
    static Output<string> subnetId = null;
    static Output<string> subnetTwoId = null;
    static Ecs.Cluster cluster = null;

    static Task<int> Main()
    {
        return Deployment.RunAsync(() => {
            ConfigureNetworking();

            if (vpcId != null)
            {
                ConfigureEcsCluster();

                ConfigureRequiredAwsServices();
            }
        });
    }

    private static void ConfigureRequiredAwsServices()
    {
        var snsTopic = new Pulumi.Aws.Sns.Topic($"{baseName}-sns1");

        var sqsQueue = new Pulumi.Aws.Sqs.Queue($"{baseName}-123-name");

        var secondRequiredQueue = new Pulumi.Aws.Sqs.Queue($"{baseName}-456-name");

        var subscription = new Pulumi.Aws.Sns.TopicSubscription($"{baseName}-sub1", new Pulumi.Aws.Sns.TopicSubscriptionArgs()
        {
            Topic = snsTopic.Arn,
            Protocol = "sqs",
            Endpoint = sqsQueue.Arn,
        });

        var attrArgs = new TableAttributesArgs()
        {
            Name = "Id",
            Type = "S",
        };

        var table = new Pulumi.Aws.DynamoDB.Table($"{baseName}-mytable", new Pulumi.Aws.DynamoDB.TableArgs()
        {
            Attributes = attrArgs,
            HashKey = "Id",
            WriteCapacity = 5,
            ReadCapacity = 5,
        });
    }

    private static void ConfigureEcsCluster()
    {
        cluster = new Ecs.Cluster($"{baseName}-cluster", null, new CustomResourceOptions());

        // Create a SecurityGroup that permits HTTP ingress and unrestricted egress.
        var webSg = new Ec2.SecurityGroup($"{baseName}-web-sg", new Ec2.SecurityGroupArgs
        {
            VpcId = vpcId,
            Egress =
                {
                    new Ec2.Inputs.SecurityGroupEgressArgs
                    {
                        Protocol = "-1",
                        FromPort = 0,
                        ToPort = 0,
                        CidrBlocks = { "0.0.0.0/0" },
                    },
                },
            Ingress =
                {
                    new Ec2.Inputs.SecurityGroupIngressArgs
                    {
                        Protocol = "tcp",
                        FromPort = 80,
                        ToPort = 80,
                        CidrBlocks = { "0.0.0.0/0" },
                    },
                },
        });

        // Create an IAM role that can be used by our service's task.
        var taskExecRole = new Iam.Role($"{baseName}-task-exec-role", new Iam.RoleArgs
        {
            AssumeRolePolicy = File.ReadAllText("perm.json"),
        });

        var taskExecAttach = new Iam.RolePolicyAttachment($"{baseName}-task-exec-policy", new Iam.RolePolicyAttachmentArgs
        {
            Role = taskExecRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy",
        });

        // Create a load balancer to listen for HTTP traffic on port 80.
        var webLb = new Elb.LoadBalancer($"{baseName}-web-lb", new Elb.LoadBalancerArgs
        {
            Subnets = new InputList<string>() { subnetId, subnetTwoId },
            SecurityGroups = { webSg.Id },
        });
        var webTg = new Elb.TargetGroup($"{baseName}-web-tg", new Elb.TargetGroupArgs
        {
            Port = 80,
            Protocol = "HTTP",
            TargetType = "ip",
            VpcId = vpcId,
        });
        var webListener = new Elb.Listener($"{baseName}-web-listener", new Elb.ListenerArgs
        {
            LoadBalancerArn = webLb.Arn,
            Port = 80,
            DefaultActions =
                {
                    new Elb.Inputs.ListenerDefaultActionsArgs
                    {
                        Type = "forward",
                        TargetGroupArn = webTg.Arn,
                    },
                },
        });

        var launchConfig = new Ec2.LaunchConfiguration($"{baseName}-launchconfig", new Ec2.LaunchConfigurationArgs()
        {
            ImageId = "ami-a1491ad2",
            InstanceType = "t2.nano",
            AssociatePublicIpAddress = true,
            SecurityGroups = webSg.Id,
        });

        var scalingGroup = new Group($"{baseName}-autoscaling", new GroupArgs()
        {
            AvailabilityZones = new InputList<string>() { "eu-west-1a", "eu-west-1b" },
            VpcZoneIdentifiers = new InputList<string>() { subnetId, subnetTwoId },
            DesiredCapacity = 1,
            LaunchConfiguration = launchConfig.Id,
            MaxSize = 1,
            MinSize = 0,
        });

        // Spin up a load balanced service running our container image.
        var appTask = new Ecs.TaskDefinition($"{baseName}-app-task", new Ecs.TaskDefinitionArgs
        {
            Family = "fargate-task-definition",
            Cpu = "256",
            Memory = "512",
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE", "EC2" },
            ExecutionRoleArn = taskExecRole.Arn,
            ContainerDefinitions = @"[{
    ""name"": ""my-app"",
    ""image"": ""nginx"",
    ""portMappings"": [{
        ""containerPort"": 80,
        ""hostPort"": 80,
        ""protocol"": ""tcp""
    }]
}]",
        });

        var ec2Svc = new Ecs.Service($"{baseName}-ec2-svc", new Ecs.ServiceArgs()
        {
            Cluster = cluster.Arn,
            DesiredCount = 1,
            LaunchType = "EC2",
            TaskDefinition = appTask.Arn,
            NetworkConfiguration = new Ecs.Inputs.ServiceNetworkConfigurationArgs
            {
                Subnets = new InputList<string>() { subnetId, subnetTwoId },
                SecurityGroups = { webSg.Id },
            },
            LoadBalancers =
                {
                    new Ecs.Inputs.ServiceLoadBalancersArgs
                    {
                        TargetGroupArn = webTg.Arn,
                        ContainerName = "my-app",
                        ContainerPort = 80,
                    },
                },
        }, new CustomResourceOptions { DependsOn = { webListener } });
    }

    private static void ConfigureNetworking()
    {
        var vpc = new Ec2.Vpc($"{baseName}-vpc", new Ec2.VpcArgs()
        {
            EnableDnsSupport = true,
            EnableDnsHostnames = true,
            CidrBlock = "10.0.0.0/16",
        });

        var subnetOne = new Ec2.Subnet($"{baseName}-subnet-one", new Ec2.SubnetArgs()
        {
            VpcId = vpc.Id,
            CidrBlock = "10.0.0.0/24",
            MapPublicIpOnLaunch = true,
            AvailabilityZone = "eu-west-1a",
        });

        var subnetTwo = new Ec2.Subnet($"{baseName}-subnet-two", new Ec2.SubnetArgs()
        {
            VpcId = vpc.Id,
            CidrBlock = "10.0.1.0/24",
            MapPublicIpOnLaunch = true,
            AvailabilityZone = "eu-west-1b",
        });

        var gateway = new Ec2.InternetGateway($"{baseName}-gateway", new Ec2.InternetGatewayArgs()
        {
            VpcId = vpc.Id,
        });

        var routeTable = new Ec2.RouteTable($"{baseName}-routetable", new Ec2.RouteTableArgs()
        {
            VpcId = vpc.Id,
        });

        var publicRoute = new Ec2.Route($"{baseName}-publicroute", new Ec2.RouteArgs()
        {
            RouteTableId = routeTable.Id,
            DestinationCidrBlock = "0.0.0.0/0",
            GatewayId = gateway.Id,
        });

        var subnetOneRouteAssociation = new Ec2.RouteTableAssociation($"{baseName}-subnetoneroutes", new Ec2.RouteTableAssociationArgs()
        {
            SubnetId = subnetOne.Id,
            RouteTableId = routeTable.Id,
        });

        vpcId = vpc.Id;
        subnetId = subnetOne.Id;
        subnetTwoId = subnetTwo.Id;
    }
}
