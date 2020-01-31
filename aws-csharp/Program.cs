using System.Collections.Generic;
using System.Threading.Tasks;

using Pulumi;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;

class Program
{
    static Task<int> Main()
    {
        return Deployment.RunAsync(() => {

            // Create a KMS Key for S3 server-side encryption
            var key = new Pulumi.Aws.Kms.Key("my-key");
            // Create an AWS resource (S3 Bucket)
            var bucket = new Bucket("my-bucket", new BucketArgs
            {
                ServerSideEncryptionConfiguration = new BucketServerSideEncryptionConfigurationArgs
                {
                    Rule = new BucketServerSideEncryptionConfigurationRuleArgs
                    {
                        ApplyServerSideEncryptionByDefault = new BucketServerSideEncryptionConfigurationRuleApplyServerSideEncryptionByDefaultArgs
                        {
                            SseAlgorithm = "aws:kms",
                            KmsMasterKeyId = key.Id,
                        },
                    },
                },
            });

            // Export the name of the bucket
            return new Dictionary<string, object>
            {
                { "bucketName", bucket.Id },
            };
        });
    }
}
