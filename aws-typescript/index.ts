import * as pulumi from "@pulumi/pulumi";
import * as aws from "@pulumi/aws";
import * as awsx from "@pulumi/awsx";
import { ApplicationTargetGroup } from "@pulumi/awsx/lb";

const vpc = new awsx.ec2.Vpc("ts-test", {});

const loadBalancer = new awsx.lb.ApplicationLoadBalancer("ts-test-lb", {
    vpc: vpc,
});

const lb = new awsx.lb.ApplicationListener("ts-test-nginx", { 
    port: 80,
    vpc: vpc,
    loadBalancer: loadBalancer,
});

const cluster = new awsx.ecs.Cluster("ts-test", { vpc });

const asg = cluster.createAutoScalingGroup("custom", {
    templateParameters: { minSize: 0, maxSize: 1, desiredCapacity: 0 },
    launchConfigurationArgs: { instanceType: "t2.micro" },
});

// Export the load balancer's address so that it's easy to access.
export const url = lb.endpoint.hostname;